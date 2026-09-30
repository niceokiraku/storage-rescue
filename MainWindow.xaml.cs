using System.Diagnostics;
using System.Globalization;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StorageRescue.Core;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace StorageRescue;

public partial class MainWindow : Window
{
    const int MaxLogLines = 3000;
    const int CellPitch = 6, CellSize = 5;

    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly Queue<(DateTime Time, long Rescued)> _rateSamples = new();

    RescueEngine? _engine;
    Mapfile? _map;
    CancellationTokenSource? _cts;
    bool _running, _closeAfterStop, _autoMapPath = true;
    DateTime _runStart;
    long _rescuedAtStart;
    DateTime _lastBeep, _lastMapRender;
    WriteableBitmap? _bitmap;
    int[] _pixels = Array.Empty<int>();

    public MainWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        Loaded += (_, _) => { RefreshDisks(); UpdateCommand(); };
        Closing += OnClosing;
    }

    // ================================================================
    //  設定
    // ================================================================

    void OnRefreshDisks(object sender, RoutedEventArgs e) => RefreshDisks();

    async void RefreshDisks()
    {
        DiskCombo.IsEnabled = false;
        var disks = await Task.Run(() => DiskEnumerator.EnumerateAll());
        int? sysDisk = DiskEnumerator.GetDiskNumberForPath(Environment.SystemDirectory);
        DiskCombo.Items.Clear();
        foreach (var d in disks)
        {
            if (d.Number == sysDisk) continue; // システムディスクは候補から外す
            DiskCombo.Items.Add(d);
        }
        DiskCombo.IsEnabled = !_running;
        AddLog($"ディスク一覧を更新: {DiskCombo.Items.Count} 台（システムディスクは除外）");
        UpdateCommand();
    }

    void OnImagePathChanged(object sender, TextChangedEventArgs e)
    {
        if (_autoMapPath && ImagePathBox.Text.Length > 0) MapPathBox.Text = ImagePathBox.Text + ".map";
        UpdateCommand();
    }

    void OnSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, MapPathBox) && MapPathBox.IsKeyboardFocused) _autoMapPath = false;
        UpdateCommand();
    }

    void OnBrowseImage(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "ディスクイメージ (*.img)|*.img|すべてのファイル|*.*", OverwritePrompt = false, FileName = "rescue.img" };
        if (dlg.ShowDialog(this) == true) ImagePathBox.Text = dlg.FileName;
    }

    void OnBrowseMap(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "mapfile (*.map)|*.map|すべてのファイル|*.*", OverwritePrompt = false };
        if (dlg.ShowDialog(this) == true) { _autoMapPath = false; MapPathBox.Text = dlg.FileName; }
    }

    void UpdateCommand()
    {
        if (!IsLoaded) return;
        try
        {
            var o = ReadOptions();
            int ss = (DiskCombo.SelectedItem as DiskInfo)?.SectorSize ?? 512;
            string img = ImagePathBox.Text.Length > 0 ? ImagePathBox.Text : "rescue.img";
            string map = MapPathBox.Text.Length > 0 ? MapPathBox.Text : "rescue.map";
            CommandText.Text = "$ " + o.ToDdrescueCommand(ss, img, map);
        }
        catch (FormatException ex) { CommandText.Text = "（設定エラー）" + ex.Message; }
    }

    RescueOptions ReadOptions()
    {
        var o = new RescueOptions
        {
            DirectIo = DirectCheck.IsChecked == true,
            NoTrim = NoTrimCheck.IsChecked == true,
            NoSweep = NoSweepCheck.IsChecked == true,
            NoScrape = NoScrapeCheck.IsChecked == true,
            RetryPasses = ParseInt(RetryBox.Text, "-r", 0, 1000),
            ClusterSectors = ParseInt(ClusterBox.Text, "-c", 0, 65536),
            ReadTimeoutSec = ParseInt(TimeoutBox.Text, "タイムアウト", 1, 600),
            PowerOffWaitSec = ParseInt(OffWaitBox.Text, "電源OFF後の待ち", 0, 600),
            SpinUpWaitSec = ParseInt(SpinUpBox.Text, "スピンアップ待ち", 0, 600),
            OfflineOnAttach = OfflineCheck.IsChecked == true,
        };
        var k = SkipBox.Text.Trim();
        if (k.Length > 0)
        {
            var parts = k.Split(',');
            if (parts[0].Length > 0) o.SkipInitial = ParseSize(parts[0]);
            if (parts.Length > 1 && parts[1].Length > 0) o.SkipMax = ParseSize(parts[1]);
        }
        return o;
    }

    static int ParseInt(string s, string name, int min, int max)
    {
        if (!int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < min || v > max)
            throw new FormatException($"{name} は {min}〜{max} の整数で指定してください");
        return v;
    }

    /// <summary>ddrescue と同様の単位（k,M,G = 1000 系、Ki,Mi,Gi = 1024 系）</summary>
    static long ParseSize(string s)
    {
        s = s.Trim();
        long mul = 1;
        (string suf, long m)[] units = { ("Ki", 1L << 10), ("Mi", 1L << 20), ("Gi", 1L << 30), ("k", 1000), ("M", 1_000_000), ("G", 1_000_000_000) };
        foreach (var (suf, m) in units)
            if (s.EndsWith(suf, StringComparison.Ordinal)) { mul = m; s = s[..^suf.Length]; break; }
        if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) || v <= 0)
            throw new FormatException("-K の値が不正です（例: 64Ki,1Gi）");
        return v * mul;
    }

    // ================================================================
    //  開始・停止
    // ================================================================

    void OnStart(object sender, RoutedEventArgs e)
    {
        if (DiskCombo.SelectedItem is not DiskInfo disk) { Warn("入力ディスクを選択してください"); return; }
        if (disk.Size <= 0) { Warn("このディスクは容量を取得できません（応答していない可能性があります）"); return; }
        string img = ImagePathBox.Text.Trim(), mapPath = MapPathBox.Text.Trim();
        if (img.Length == 0 || mapPath.Length == 0) { Warn("出力イメージと mapfile のパスを指定してください"); return; }

        RescueOptions opt;
        try { opt = ReadOptions(); } catch (FormatException ex) { Warn(ex.Message); return; }

        // 保存先が入力ディスク上にないか
        foreach (var p in new[] { img, mapPath })
        {
            var dir = IOPath.GetDirectoryName(IOPath.GetFullPath(p));
            if (dir == null || !Directory.Exists(dir)) { Warn($"フォルダが存在しません: {dir}"); return; }
            if (DiskEnumerator.GetDiskNumberForPath(dir) == disk.Number)
            {
                Warn("保存先が入力ディスク上にあります。別のディスクを指定してください。");
                return;
            }
        }

        // mapfile の読み込み（再開）または新規作成
        Mapfile map;
        string resumeInfo;
        if (File.Exists(mapPath))
        {
            try { map = Mapfile.Load(mapPath, disk.Size); }
            catch (Exception ex) { Warn($"mapfile を読み込めません:\n{ex.Message}"); return; }
            var t = map.Totals();
            resumeInfo = $"既存の mapfile から再開します（rescued {Units.Bytes(t.Finished)} / {Units.Bytes(disk.Size)}）";
        }
        else
        {
            if (File.Exists(img) && MessageBox.Show(this,
                    "出力イメージは存在しますが mapfile がありません。\n既存のイメージに上書きで書き込みます。続行しますか？",
                    "確認", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            map = new Mapfile(disk.Size);
            resumeInfo = "新規に開始します";
        }

        if (MessageBox.Show(this,
                $"入力: {disk}\n出力: {img}\nmapfile: {mapPath}\n\n{resumeInfo}\n\n入力ディスクへの書き込みは行いません。開始しますか？",
                "開始の確認", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        // 再接続のたびに Windows がマウントしないよう、開始前に永続オフライン化しておく
        if (opt.OfflineOnAttach && !disk.Offline && !ApplyOffline(disk) &&
            MessageBox.Show(this, "オフライン化できませんでした。再接続時に Windows がマウントする可能性があります。\n続行しますか？",
                "確認", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var engine = new RescueEngine(disk, img, mapPath, map, opt);
        engine.Log += msg => Dispatcher.BeginInvoke(() => AddLog(msg));
        engine.Attention += msg => Dispatcher.BeginInvoke(() => OnAttention(msg));

        _engine = engine;
        _map = map;
        _cts = new CancellationTokenSource();
        _runStart = DateTime.Now;
        _rescuedAtStart = map.Totals().Finished;
        _rateSamples.Clear();
        SetRunning(true);

        var ct = _cts.Token;
        Task.Factory.StartNew(() => engine.Run(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            .ContinueWith(t =>
            {
                if (t.Exception != null) AddLog("エラー: " + t.Exception.GetBaseException().Message);
                engine.Dispose();
                if (opt.OfflineOnAttach)
                    AddLog("入力ディスクはオフラインのままです。通常の利用に戻すときは「オンラインに戻す」を押してください");
                SetRunning(false);
                OnTick();
                if (_closeAfterStop) Close();
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void OnStop(object sender, RoutedEventArgs e)
    {
        if (_cts == null || _cts.IsCancellationRequested) return;
        _cts.Cancel();
        StopButton.IsEnabled = false;
        AddLog("停止しています…（読み取り中の場合はタイムアウトまで待ちます）");
    }

    void OnConfirmPowerOff(object sender, RoutedEventArgs e) => _engine?.ConfirmPowerOff();

    void SetRunning(bool running)
    {
        _running = running;
        SettingsBox.IsEnabled = !running;
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        OfflineButton.IsEnabled = OnlineButton.IsEnabled = !running;
        if (!running) Overlay.Visibility = Visibility.Collapsed;
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_running) return;
        if (MessageBox.Show(this, "処理中です。停止して終了しますか？（mapfile は保存されます）", "確認",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        e.Cancel = true;
        _closeAfterStop = true;
        OnStop(this, new RoutedEventArgs());
    }

    // ================================================================
    //  オフライン化（マウント防止）
    // ================================================================

    void OnSetOffline(object sender, RoutedEventArgs e)
    {
        if (DiskCombo.SelectedItem is not DiskInfo disk) { Warn("入力ディスクを選択してください"); return; }
        if (MessageBox.Show(this,
                $"{disk}\n\nこのディスクをオフライン＋読み取り専用にします。\n" +
                "設定はディスクごとに保存され、電源の入れ直し・再接続後もマウントされなくなります。\n" +
                "（ドライブ文字が付いている場合は消えます。開いているファイルは閉じてください）\n\n実行しますか？",
                "確認", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ApplyOffline(disk);
        RefreshDisks();
    }

    void OnSetOnline(object sender, RoutedEventArgs e)
    {
        if (DiskCombo.SelectedItem is not DiskInfo disk) { Warn("ディスクを選択してください"); return; }
        if (MessageBox.Show(this,
                $"{disk}\n\nこのディスクをオンライン＋書き込み可能に戻します。\n" +
                "Windows がボリュームをマウントし、ファイルシステムへのアクセスが始まります。\n" +
                "障害ディスクの場合、書き込み（chkdsk など）で状態が悪化するおそれがあります。\n\n実行しますか？",
                "確認", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (DiskEnumerator.TrySetOffline(disk.Number, offline: false, persist: true, out var err))
            AddLog($"PhysicalDrive{disk.Number} をオンラインに戻しました");
        else
            AddLog($"オンライン化に失敗: {err}");
        RefreshDisks();
    }

    bool ApplyOffline(DiskInfo disk)
    {
        if (!DiskEnumerator.TrySetOffline(disk.Number, offline: true, persist: true, out var err))
        {
            AddLog($"オフライン化に失敗: {err}");
            return false;
        }
        bool? state = DiskEnumerator.IsOffline(disk.Number);
        AddLog(state == true
            ? $"PhysicalDrive{disk.Number} をオフライン＋読み取り専用にしました（再接続後も維持されます）"
            : $"PhysicalDrive{disk.Number} のオフライン化を要求しましたが、状態を確認できませんでした");
        return state == true;
    }

    // ================================================================
    //  通知
    // ================================================================

    void OnAttention(string message)
    {
        _lastBeep = DateTime.Now;
        SystemSounds.Exclamation.Play();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = new WindowInteropHelper(this).Handle,
            dwFlags = 3 | 12, // FLASHW_ALL | FLASHW_TIMERNOFG
        };
        Native.FlashWindowEx(ref info);
    }

    // ================================================================
    //  表示更新
    // ================================================================

    void OnTick()
    {
        var eng = _engine;
        var map = _map;
        if (map == null) return;
        var t = map.Totals();
        var now = DateTime.Now;

        // 速度
        _rateSamples.Enqueue((now, t.Finished));
        while (_rateSamples.Count > 2 && (now - _rateSamples.Peek().Time).TotalSeconds > 5) _rateSamples.Dequeue();
        var first = _rateSamples.Peek();
        double span = (now - first.Time).TotalSeconds;
        double curRate = span > 0.5 ? (t.Finished - first.Rescued) / span : 0;
        double runSec = Math.Max(1, (now - _runStart).TotalSeconds);
        double avgRate = (t.Finished - _rescuedAtStart) / runSec;
        long remaining = t.NonTried + t.NonTrimmed + t.NonScraped;
        string remain = curRate > 0 ? Units.Duration(TimeSpan.FromSeconds(remaining / curRate)) : "n/a";
        double pct = map.Size > 0 ? 100.0 * t.Finished / map.Size : 0;

        long ipos = eng?.IPos ?? map.CurrentPos;
        string C(string label, string value) => $"{label,13}: {value,-12}";
        StatusText.Text =
            C("ipos", Units.Bytes(ipos)) + C("non-trimmed", Units.Bytes(t.NonTrimmed)) + C("current rate", Units.Bytes(curRate) + "/s") + "\n" +
            C("opos", Units.Bytes(ipos)) + C("non-scraped", Units.Bytes(t.NonScraped)) + C("average rate", Units.Bytes(avgRate) + "/s") + "\n" +
            C("non-tried", Units.Bytes(t.NonTried)) + C("bad-sector", Units.Bytes(t.BadSector)) + C("run time", Units.Duration(now - _runStart)) + "\n" +
            C("rescued", Units.Bytes(t.Finished)) + C("bad areas", t.BadAreas.ToString()) + C("remaining time", remain) + "\n" +
            C("pct rescued", pct.ToString("0.00") + "%") + C("read errors", (eng?.ReadErrors ?? 0).ToString()) +
            C("since last OK", eng != null ? Units.Duration(now - eng.LastSuccess) : "-") + "\n" +
            C("disconnects", (eng?.Disconnects ?? 0).ToString()) + C("power cycles", (eng?.PowerCycles ?? 0).ToString()) + C("map blocks", t.BlockCount.ToString());
        PhaseText.Text = _running ? eng?.PhaseText ?? "" : (map.CurrentStatus == RescuePhase.Finished ? "完了" : "停止中");

        UpdateOverlay(eng?.Recovery);

        if ((now - _lastMapRender).TotalSeconds >= 1) { _lastMapRender = now; RenderMap(map, ipos); }
    }

    void UpdateOverlay(RecoveryState? r)
    {
        if (!_running || r == null) { Overlay.Visibility = Visibility.Collapsed; return; }
        Overlay.Visibility = Visibility.Visible;
        bool manual = r.Step >= RecoveryStep.AskPowerOff;
        OverlayStep.Text = manual ? $"電源の入れ直し（{r.PowerCycles} 回目）" : "自動復旧中";
        OverlayTitle.Text = r.Title;
        OverlayMessage.Text = r.Message;
        ConfirmOffButton.Visibility = r.AllowConfirmOff ? Visibility.Visible : Visibility.Collapsed;
        if (r.CountdownEnd is DateTime end)
        {
            OverlayCountdown.Visibility = Visibility.Visible;
            OverlayCountdown.Text = $"{Math.Max(0, (int)Math.Ceiling((end - DateTime.Now).TotalSeconds))}";
        }
        else OverlayCountdown.Visibility = Visibility.Collapsed;

        // ユーザー操作待ちの間は 30 秒ごとに再度鳴らす
        if (r.Step is RecoveryStep.AskPowerOff or RecoveryStep.AskPowerOn && (DateTime.Now - _lastBeep).TotalSeconds >= 30)
        {
            _lastBeep = DateTime.Now;
            SystemSounds.Exclamation.Play();
        }
    }

    void OnMapHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _bitmap = null;
        if (_map != null) RenderMap(_map, _engine?.IPos ?? _map.CurrentPos);
    }

    void RenderMap(Mapfile map, long ipos)
    {
        int w = (int)MapHost.ActualWidth - 8, h = (int)MapHost.ActualHeight - 8;
        if (w < CellPitch || h < CellPitch || map.Size <= 0) return;
        int cols = w / CellPitch, rows = h / CellPitch;
        long cells = (long)cols * rows;
        long cellBytes = Math.Max(1, (map.Size + cells - 1) / cells);
        int used = (int)((map.Size + cellBytes - 1) / cellBytes);

        // 1 セル内に複数状態があるときは「悪い方」を表示（ddrescueview と同様）
        var prio = new sbyte[used];
        foreach (var b in map.Snapshot())
        {
            sbyte p = b.Status switch
            {
                BlockStatus.Finished => 0, BlockStatus.NonTried => 1, BlockStatus.NonTrimmed => 2,
                BlockStatus.NonScraped => 3, _ => 4,
            };
            long c0 = b.Pos / cellBytes, c1 = (b.End - 1) / cellBytes;
            for (long c = c0; c <= c1 && c < used; c++) if (p > prio[c]) prio[c] = p;
        }
        int[] colors = { unchecked((int)0xFF3FB950), unchecked((int)0xFF6E7681), unchecked((int)0xFFD29922), unchecked((int)0xFF58A6FF), unchecked((int)0xFFF85149) };
        long curCell = _running ? ipos / cellBytes : -1;

        int bw = cols * CellPitch, bh = rows * CellPitch;
        if (_bitmap == null || _bitmap.PixelWidth != bw || _bitmap.PixelHeight != bh)
        {
            _bitmap = new WriteableBitmap(bw, bh, 96, 96, PixelFormats.Bgra32, null);
            _pixels = new int[bw * bh];
            MapImage.Source = _bitmap;
            MapImage.Margin = new Thickness(4);
        }
        Array.Clear(_pixels);
        for (int c = 0; c < used; c++)
        {
            int color = c == curCell ? unchecked((int)0xFFFFFFFF) : colors[prio[c]];
            int x0 = c % cols * CellPitch, y0 = c / cols * CellPitch;
            for (int y = 0; y < CellSize; y++)
            {
                int row = (y0 + y) * bw + x0;
                for (int x = 0; x < CellSize; x++) _pixels[row + x] = color;
            }
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, bw, bh), _pixels, bw * 4, 0);
        MapImage.ToolTip = $"1 セル = {Units.Bytes(cellBytes)}";
    }

    // ================================================================

    void AddLog(string message)
    {
        LogList.Items.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        while (LogList.Items.Count > MaxLogLines) LogList.Items.RemoveAt(0);
        LogList.ScrollIntoView(LogList.Items[^1]);
    }

    void Warn(string message) => MessageBox.Show(this, message, "StorageRescue", MessageBoxButton.OK, MessageBoxImage.Warning);
}

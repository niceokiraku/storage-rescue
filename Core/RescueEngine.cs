using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StorageRescue.Core;

public enum RecoveryStep { AutoReconnect, Rescan, AskPowerOff, WaitAfterPowerOff, AskPowerOn, SpinUp }

public sealed record RecoveryState(
    RecoveryStep Step, string Title, string Message, DateTime? CountdownEnd, int PowerCycles, bool AllowConfirmOff);

public sealed class DestinationException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// GNU ddrescue のアルゴリズム（copying → trimming → sweeping → scraping → retrying）に、
/// 切断検知と「自動再接続 → 再スキャン → 手動での電源入れ直し案内」を組み合わせたエンジン。
/// </summary>
public sealed unsafe class RescueEngine : IDisposable
{
    readonly DiskInfo _target;
    readonly string _imagePath, _mapPath, _cmdLine;
    readonly RescueOptions _o;
    readonly int _ss, _clusterBytes;
    readonly long _skipInitial, _skipMax;
    readonly DateTime _startTime = DateTime.Now;
    readonly Stopwatch _saveWatch = Stopwatch.StartNew();

    RawDisk? _disk;
    byte* _buf;
    FileStream? _out;
    int _consecutiveTimeouts;
    volatile bool _confirmPowerOff;

    public Mapfile Map { get; }
    public RecoveryState? Recovery { get; private set; }
    public string PhaseText { get; private set; } = "";
    public long IPos { get; private set; }
    public long ReadErrors { get; private set; }
    public int Disconnects { get; private set; }
    public int PowerCycles { get; private set; }
    public DateTime LastSuccess { get; private set; } = DateTime.Now;
    public int SectorSize => _ss;

    public event Action<string>? Log;
    public event Action<string>? Attention;

    public RescueEngine(DiskInfo target, string imagePath, string mapPath, Mapfile map, RescueOptions options)
    {
        _target = target;
        _imagePath = imagePath;
        _mapPath = mapPath;
        _o = options;
        Map = map;
        _ss = target.SectorSize;

        _clusterBytes = _o.ClusterSectors > 0 ? _o.ClusterSectors * _ss : Math.Max(_ss, 65536 / _ss * _ss);
        long size = target.Size;
        _skipInitial = AlignUp(_o.SkipInitial > 0 ? _o.SkipInitial : Math.Max(65536, size / 32768));
        _skipMax = AlignUp(Math.Max(_skipInitial, _o.SkipMax > 0 ? _o.SkipMax : size / 100));
        _cmdLine = $"StorageRescue PhysicalDrive{target.Number} \"{imagePath}\" \"{mapPath}\"  (≈ {_o.ToDdrescueCommand(_ss, imagePath, mapPath)})";
        _buf = AllocBuffer();
    }

    byte* AllocBuffer() => (byte*)NativeMemory.AlignedAlloc((nuint)_clusterBytes, 4096);
    long AlignUp(long v) => (v + _ss - 1) / _ss * _ss;

    public void ConfirmPowerOff() => _confirmPowerOff = true;

    // ================================================================
    //  メインループ
    // ================================================================

    public void Run(CancellationToken ct)
    {
        _out = new FileStream(_imagePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 1);
        try
        {
            Log?.Invoke($"開始: {_target}");
            Log?.Invoke($"cluster={_clusterBytes / _ss} sectors ({Units.Bytes(_clusterBytes)}), skip={Units.Bytes(_skipInitial)}〜{Units.Bytes(_skipMax)}, timeout={_o.ReadTimeoutSec}s, direct={(_o.DirectIo ? "on (-d)" : "off")}");
            if (!TryConnect()) Recover(ct);

            // 中断した copying pass から再開
            bool resumeCopy = Map.CurrentStatus == RescuePhase.Copying;
            int resumePass = resumeCopy ? Math.Clamp(Map.CurrentPass, 1, 2) : 1;
            long resumePos = resumeCopy ? Map.CurrentPos : 0;

            if (resumePass == 1)
            {
                SetPhase(RescuePhase.Copying, 1, "Copying pass 1 (forward, skipping)");
                CopyForward(resumePos, skipping: true, ct);
            }
            SetPhase(RescuePhase.Copying, 2, "Copying pass 2 (reverse)");
            CopyReverse(resumePass == 2 ? Math.Min(Map.Size, resumePos + _clusterBytes) : Map.Size, ct);

            if (!_o.NoTrim) Trim(ct);
            if (!_o.NoSweep)
            {
                SetPhase(RescuePhase.Sweeping, 1, "Sweeping (forward, no skipping)");
                CopyForward(0, skipping: false, ct);
                if (!_o.NoTrim) Trim(ct);
            }
            if (!_o.NoScrape) Scrape(ct);
            for (int p = 1; p <= _o.RetryPasses; p++) Retry(p, ct);

            SetPhase(RescuePhase.Finished, 1, "Finished");
            var t = Map.Totals();
            Log?.Invoke($"完了: rescued {Units.Bytes(t.Finished)}, bad-sector {Units.Bytes(t.BadSector)}");
        }
        catch (OperationCanceledException)
        {
            Log?.Invoke("停止しました（mapfile を保存して再開できます）");
        }
        catch (DestinationException ex)
        {
            Log?.Invoke($"保存先への書き込みに失敗したため停止しました: {ex.Message}");
            Attention?.Invoke("保存先への書き込みに失敗しました");
        }
        finally
        {
            Recovery = null;
            TrySaveMap();
            _disk?.Dispose();
            _disk = null;
            _out.Dispose();
        }
    }

    void SetPhase(RescuePhase phase, int pass, string text)
    {
        Map.CurrentStatus = phase;
        Map.CurrentPass = pass;
        PhaseText = text;
        Log?.Invoke($"--- {text} ---");
    }

    // ================================================================
    //  各フェーズ（GNU ddrescue の Algorithm 節に準拠）
    // ================================================================

    /// <summary>Pass 1 / Sweeping: 前方向に未読領域を読む。skipping=true なら失敗後に指数的にスキップ。</summary>
    void CopyForward(long pos, bool skipping, CancellationToken ct)
    {
        long skip = 0;
        while (Map.FindNext(pos, BlockStatus.NonTried) is Block b)
        {
            pos = b.Pos;
            int len = (int)Math.Min(_clusterBytes, b.Size);
            if (ReadChunk(pos, len, ct))
            {
                Map.SetStatus(pos, len, BlockStatus.Finished);
                pos += len;
                skip = 0;
            }
            else
            {
                Map.SetStatus(pos, len, BlockStatus.NonTrimmed);
                pos += len;
                if (skipping)
                {
                    skip = skip == 0 ? _skipInitial : Math.Min(skip * 2, _skipMax);
                    pos = Math.Min(Map.Size, pos + skip);
                }
            }
        }
    }

    /// <summary>Pass 2: 逆方向。各未読ブロックを末尾から読み、最初のエラーでそのブロックの残りを飛ばす。</summary>
    void CopyReverse(long pos, CancellationToken ct)
    {
        while (Map.FindPrev(pos, BlockStatus.NonTried) is Block b)
        {
            long end = b.End;
            while (end > b.Pos)
            {
                long start = Math.Max(b.Pos, end - _clusterBytes);
                int len = (int)(end - start);
                bool ok = ReadChunk(start, len, ct);
                Map.SetStatus(start, len, ok ? BlockStatus.Finished : BlockStatus.NonTrimmed);
                end = start;
                if (!ok) break;
            }
            pos = b.Pos;
        }
    }

    /// <summary>Trimming: 失敗ブロックの両端をセクタ単位で読み、エラーに当たるまで削る。中央は non-scraped。</summary>
    void Trim(CancellationToken ct)
    {
        if (Map.FindNext(0, BlockStatus.NonTrimmed) is null) return;
        SetPhase(RescuePhase.Trimming, 1, "Trimming");
        long pos = 0;
        while (Map.FindNext(pos, BlockStatus.NonTrimmed) is Block b)
        {
            long a = b.Pos, e = b.End;
            while (a < e)
            {
                bool ok = ReadChunk(a, _ss, ct);
                Map.SetStatus(a, _ss, ok ? BlockStatus.Finished : BlockStatus.BadSector);
                a += _ss;
                if (!ok) break;
            }
            while (e > a)
            {
                bool ok = ReadChunk(e - _ss, _ss, ct);
                Map.SetStatus(e - _ss, _ss, ok ? BlockStatus.Finished : BlockStatus.BadSector);
                e -= _ss;
                if (!ok) break;
            }
            if (e > a) Map.SetStatus(a, e - a, BlockStatus.NonScraped);
            pos = b.End;
        }
    }

    /// <summary>Scraping: non-scraped 領域をセクタ単位で 1 パス読む。</summary>
    void Scrape(CancellationToken ct)
    {
        if (Map.FindNext(0, BlockStatus.NonScraped) is null) return;
        SetPhase(RescuePhase.Scraping, 1, "Scraping");
        long pos = 0;
        while (Map.FindNext(pos, BlockStatus.NonScraped) is Block b)
        {
            for (long p = b.Pos; p < b.End; p += _ss)
            {
                bool ok = ReadChunk(p, _ss, ct);
                Map.SetStatus(p, _ss, ok ? BlockStatus.Finished : BlockStatus.BadSector);
            }
            pos = b.End;
        }
    }

    /// <summary>Retrying: bad-sector を読み直す。パスごとに方向を反転。</summary>
    void Retry(int pass, CancellationToken ct)
    {
        if (Map.FindNext(0, BlockStatus.BadSector) is null) return;
        bool reverse = pass % 2 == 0;
        SetPhase(RescuePhase.Retrying, pass, $"Retrying pass {pass} ({(reverse ? "reverse" : "forward")})");
        if (!reverse)
        {
            long pos = 0;
            while (Map.FindNext(pos, BlockStatus.BadSector) is Block b)
            {
                for (long p = b.Pos; p < b.End; p += _ss)
                    if (ReadChunk(p, _ss, ct)) Map.SetStatus(p, _ss, BlockStatus.Finished);
                pos = b.End;
            }
        }
        else
        {
            long pos = Map.Size;
            while (Map.FindPrev(pos, BlockStatus.BadSector) is Block b)
            {
                for (long p = b.End - _ss; p >= b.Pos; p -= _ss)
                    if (ReadChunk(p, _ss, ct)) Map.SetStatus(p, _ss, BlockStatus.Finished);
                pos = b.Pos;
            }
        }
    }

    // ================================================================
    //  読み取りと切断判定
    // ================================================================

    /// <summary>
    /// 1 回読む。成功ならイメージに書いて true。
    /// 失敗が「切断」と判定された場合は復旧（再接続〜電源入れ直し案内）まで行ってから false を返す。
    /// 切断を起こした領域は失敗扱いにするので、同じ場所で何度も落ちるのを避けられる。
    /// </summary>
    bool ReadChunk(long pos, int len, CancellationToken ct)
    {
        Checkpoint(ct);
        if (_disk == null) Recover(ct);

        IPos = pos;
        Map.CurrentPos = pos;
        var r = _disk!.Read(pos, _buf, len, _o.ReadTimeoutSec * 1000);
        if (r.Status == ReadStatus.Ok)
        {
            WriteOut(pos, len);
            LastSuccess = DateTime.Now;
            _consecutiveTimeouts = 0;
            return true;
        }

        ReadErrors++;
        if (IsDisconnect(r))
        {
            Disconnects++;
            Log?.Invoke($"切断を検知: pos 0x{pos:X} ({Describe(r)})");
            DropDisk();
            Recover(ct);
        }
        else
        {
            Log?.Invoke($"読み取りエラー: pos 0x{pos:X} size {len} ({Describe(r)})");
        }
        return false;
    }

    bool IsDisconnect(ReadResult r)
    {
        switch (r.Status)
        {
            case ReadStatus.Hung:
                return true;
            case ReadStatus.Timeout:
                // 連続タイムアウトはファームウェアが固まっている可能性が高い
                if (++_consecutiveTimeouts >= 3) return true;
                return !StillAttached();
        }
        if (IsMediaError(r.Win32Error)) { _consecutiveTimeouts = 0; return false; }
        return r.Win32Error switch
        {
            2 or 6 or 21 or 55 or 433 or 1112 or 1167 => true, // FILE_NOT_FOUND, INVALID_HANDLE, NOT_READY, DEV_NOT_EXIST, NO_SUCH_DEVICE, NO_MEDIA, DEVICE_NOT_CONNECTED
            _ => !StillAttached(),                              // GEN_FAILURE(31), IO_DEVICE(1117) 等は実在確認で判定
        };
    }

    static bool IsMediaError(int code) => code is 23 or 25 or 27 or 30; // CRC, SEEK, SECTOR_NOT_FOUND, READ_FAULT

    bool StillAttached()
    {
        var now = DiskEnumerator.FindSameDisk(_target);
        return now != null && _disk != null && now.Number == _disk.Number;
    }

    static string Describe(ReadResult r) => r.Status switch
    {
        ReadStatus.Timeout => "timeout",
        ReadStatus.Hung => "I/O hung (cancel failed)",
        _ => r.Win32Error == -1 ? "short read" : $"{new System.ComponentModel.Win32Exception(r.Win32Error).Message} [{r.Win32Error}]",
    };

    void WriteOut(long pos, int len)
    {
        try
        {
            _out!.Position = pos;
            _out.Write(new ReadOnlySpan<byte>(_buf, len));
        }
        catch (IOException ex) { throw new DestinationException(ex.Message, ex); }
    }

    void DropDisk()
    {
        if (_disk == null) return;
        if (_disk.Poisoned)
        {
            // 固まった I/O がまだこのバッファを使う可能性があるので、捨てて新しく確保する
            _buf = AllocBuffer();
        }
        _disk.Dispose();
        _disk = null;
    }

    // ================================================================
    //  復旧（L1 自動再接続 → L2 再スキャン → L3 手動での電源入れ直し）
    // ================================================================

    void Recover(CancellationToken ct)
    {
        TrySaveMap();
        try
        {
            SetRecovery(RecoveryStep.AutoReconnect, "自動で再接続しています",
                "デバイスが応答するのを待っています…", 10);
            if (WaitConnect(10, ct)) return;

            SetRecovery(RecoveryStep.Rescan, "ハードウェアを再スキャンしています",
                "デバイスマネージャーの「ハードウェア変更のスキャン」を実行しました。", 15);
            Log?.Invoke("ハードウェア変更のスキャンを実行");
            DiskEnumerator.RescanHardware();
            if (WaitConnect(15, ct)) return;

            while (true)
            {
                PowerCycles++;
                _confirmPowerOff = false;
                Log?.Invoke($"電源の入れ直しを依頼（{PowerCycles} 回目）");
                Attention?.Invoke("HDDの電源を入れ直してください");

                if (DiskEnumerator.FindSameDisk(_target) != null)
                {
                    SetRecovery(RecoveryStep.AskPowerOff, "① HDDの電源を切ってください",
                        "電源が切れたことを検知すると、自動的に次へ進みます。\n検知されない場合は、下のボタンを押してください。",
                        null, allowConfirmOff: true);
                    while (!_confirmPowerOff && DiskEnumerator.FindSameDisk(_target) != null) Sleep(1000, ct);
                    Log?.Invoke(_confirmPowerOff ? "電源OFF（ユーザー操作で確認）" : "電源OFFを検知");

                    SetRecovery(RecoveryStep.WaitAfterPowerOff, "② まだ電源を入れないでください",
                        "ディスクが完全に停止するまで待っています。", _o.PowerOffWaitSec);
                    Sleep(_o.PowerOffWaitSec * 1000, ct);

                    SetRecovery(RecoveryStep.AskPowerOn, "③ HDDの電源を入れてください",
                        "接続を検知すると、自動的に再開します。", null);
                }
                else
                {
                    SetRecovery(RecoveryStep.AskPowerOn, "HDDの電源を入れ直してください",
                        $"デバイスはすでに切断されています。\nHDDの電源を切り、{_o.PowerOffWaitSec} 秒以上待ってから電源を入れてください。\n接続を検知すると、自動的に再開します。",
                        null);
                }
                while (DiskEnumerator.FindSameDisk(_target) == null) Sleep(1000, ct);
                Log?.Invoke("ディスクの接続を検知");

                SetRecovery(RecoveryStep.SpinUp, "④ 回転が安定するのを待っています",
                    "このまま触らずにお待ちください。", _o.SpinUpWaitSec);
                Sleep(_o.SpinUpWaitSec * 1000, ct);
                if (WaitConnect(20, ct)) return;

                Log?.Invoke("再接続後の読み取り確認に失敗。もう一度電源の入れ直しを依頼します");
            }
        }
        finally
        {
            Recovery = null;
        }
    }

    void SetRecovery(RecoveryStep step, string title, string message, int? countdownSec, bool allowConfirmOff = false)
    {
        if (PowerCycles >= _o.PowerCycleWarnCount && step >= RecoveryStep.AskPowerOff)
            message += $"\n\n⚠ 電源の入れ直しが {PowerCycles} 回に達しました。異音（カチカチ音など）がする場合は中止を検討してください。";
        Recovery = new RecoveryState(step, title, message,
            countdownSec is int s ? DateTime.Now.AddSeconds(s) : null, PowerCycles, allowConfirmOff);
    }

    bool WaitConnect(int seconds, CancellationToken ct)
    {
        var deadline = DateTime.Now.AddSeconds(seconds);
        while (true)
        {
            if (TryConnect()) return true;
            if (DateTime.Now >= deadline) return false;
            Sleep(1000, ct);
        }
    }

    /// <summary>同じディスク（モデル・シリアル・容量が一致）を探して開き、1 セクタ読めるか確認する</summary>
    bool TryConnect()
    {
        var info = DiskEnumerator.FindSameDisk(_target);
        if (info == null) return false;

        // 通常は開始前に永続オフライン化済み。デバイスの再認識で設定が引き継がれなかった場合に備えて掛け直す
        if (_o.OfflineOnAttach && DiskEnumerator.IsOffline(info.Number) != true)
        {
            if (DiskEnumerator.TrySetOffline(info.Number, offline: true, persist: true, out var err))
                Log?.Invoke($"PhysicalDrive{info.Number} をオフライン＋読み取り専用にしました");
            else
                Log?.Invoke($"オフライン化に失敗（続行します）: {err}");
        }

        RawDisk d;
        try { d = RawDisk.Open(info.Number, _o.DirectIo); }
        catch (Exception ex) { Log?.Invoke($"PhysicalDrive{info.Number} を開けません: {ex.Message}"); return false; }

        // 読めたことがある場所（なければ LBA 0）で応答を確認。メディアエラーでも「応答あり」とみなす。
        long vpos = Map.FirstFinishedPos();
        var r = d.Read(vpos, _buf, _ss, Math.Min(_o.ReadTimeoutSec, 5) * 1000);
        if (r.Status == ReadStatus.Ok || (r.Status == ReadStatus.Error && IsMediaError(r.Win32Error)))
        {
            _disk = d;
            _consecutiveTimeouts = 0;
            Log?.Invoke($"接続OK: PhysicalDrive{info.Number}");
            return true;
        }

        Log?.Invoke($"接続確認の読み取りに失敗: {Describe(r)}");
        if (d.Poisoned) _buf = AllocBuffer();
        d.Dispose();
        return false;
    }

    // ================================================================

    void Checkpoint(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_saveWatch.Elapsed.TotalSeconds >= _o.MapSaveIntervalSec) TrySaveMap();
    }

    void TrySaveMap()
    {
        try
        {
            _out?.Flush(true); // イメージを先に確定させてから mapfile を更新
            Map.Save(_mapPath, _cmdLine, _startTime);
        }
        catch (Exception ex) { Log?.Invoke($"mapfile の保存に失敗: {ex.Message}"); }
        _saveWatch.Restart();
    }

    static void Sleep(int ms, CancellationToken ct)
    {
        ct.WaitHandle.WaitOne(ms);
        ct.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        _disk?.Dispose();
        if (_buf != null) { NativeMemory.AlignedFree(_buf); _buf = null; }
    }
}

namespace StorageRescue.Core;

/// <summary>GNU ddrescue のオプションに対応させた設定＋再接続関連の独自設定</summary>
public sealed class RescueOptions
{
    // ---- ddrescue 相当 ----
    public bool DirectIo { get; set; } = true; // -d, --idirect（FILE_FLAG_NO_BUFFERING）
    public bool NoTrim { get; set; }          // -N, --no-trim
    public bool NoSweep { get; set; }         // --no-sweep
    public bool NoScrape { get; set; }        // -n, --no-scrape
    public int RetryPasses { get; set; }      // -r, --retry-passes
    public int ClusterSectors { get; set; }   // -c, --cluster-size（0 = 自動: 64 KiB / sector_size）
    public long SkipInitial { get; set; }     // -K initial（0 = 自動: max(64 KiB, 容量/32768)）
    public long SkipMax { get; set; }         // -K ,max  （0 = 自動: 容量の 1%）

    // ---- 独自（切断・再接続まわり）----
    public int ReadTimeoutSec { get; set; } = 10;
    public int PowerOffWaitSec { get; set; } = 10;
    public int SpinUpWaitSec { get; set; } = 15;
    public bool OfflineOnAttach { get; set; } = true;
    public int MapSaveIntervalSec { get; set; } = 30;
    public int PowerCycleWarnCount { get; set; } = 20;

    /// <summary>参考用：同じ設定を Linux の GNU ddrescue で実行する場合のコマンドライン</summary>
    public string ToDdrescueCommand(int sectorSize, string image, string map)
    {
        var a = new List<string> { "ddrescue" };
        if (DirectIo) a.Add("-d");
        a.Add($"-b {sectorSize}");
        if (ClusterSectors > 0) a.Add($"-c {ClusterSectors}");
        if (SkipInitial > 0 || SkipMax > 0)
            a.Add($"-K {(SkipInitial > 0 ? SkipInitial : "")}{(SkipMax > 0 ? "," + SkipMax : "")}");
        if (NoTrim) a.Add("-N");
        if (NoSweep) a.Add("--no-sweep");
        if (NoScrape) a.Add("-n");
        if (RetryPasses > 0) a.Add($"-r {RetryPasses}");
        a.Add("/dev/sdX");
        a.Add(Quote(Path.GetFileName(image)));
        a.Add(Quote(Path.GetFileName(map)));
        return string.Join(' ', a);
    }

    static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}

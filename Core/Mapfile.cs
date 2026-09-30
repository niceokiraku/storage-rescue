using System.Globalization;
using System.Text;

namespace StorageRescue.Core;

/// <summary>GNU ddrescue のブロック状態文字</summary>
public enum BlockStatus : byte
{
    NonTried = (byte)'?',
    NonTrimmed = (byte)'*',
    NonScraped = (byte)'/',
    BadSector = (byte)'-',
    Finished = (byte)'+',
}

/// <summary>GNU ddrescue の current_status 文字</summary>
public enum RescuePhase : byte
{
    Copying = (byte)'?',
    Trimming = (byte)'*',
    Sweeping = (byte)'%',
    Scraping = (byte)'/',
    Retrying = (byte)'-',
    Finished = (byte)'+',
}

public readonly record struct Block(long Pos, long Size, BlockStatus Status)
{
    public long End => Pos + Size;
}

public readonly record struct MapTotals(
    long NonTried, long NonTrimmed, long NonScraped, long BadSector, long Finished, int BadAreas, int BlockCount);

/// <summary>
/// GNU ddrescue 互換の mapfile。
/// 形式: コメント行(#) → status 行 "current_pos current_status current_pass" → ブロック行 "pos size status"
/// 途中で Linux の ddrescue に引き継げるよう、書式を ddrescue に合わせている。
/// </summary>
public sealed class Mapfile
{
    readonly List<Block> _blocks = new();
    readonly object _lock = new();

    public long Size { get; }
    public long CurrentPos { get; set; }
    public RescuePhase CurrentStatus { get; set; } = RescuePhase.Copying;
    public int CurrentPass { get; set; } = 1;

    public Mapfile(long size)
    {
        Size = size;
        if (size > 0) _blocks.Add(new Block(0, size, BlockStatus.NonTried));
    }

    // ---- 更新 ----

    public void SetStatus(long pos, long size, BlockStatus status)
    {
        long end = Math.Min(pos + size, Size);
        pos = Math.Max(pos, 0);
        if (end <= pos) return;
        lock (_lock)
        {
            SplitAt(pos);
            SplitAt(end);
            int i = FindIndex(pos);
            int j = i;
            while (j < _blocks.Count && _blocks[j].Pos < end) j++;
            _blocks.RemoveRange(i, j - i);
            _blocks.Insert(i, new Block(pos, end - pos, status));
            // 同じ状態の隣接ブロックを結合
            if (i + 1 < _blocks.Count && _blocks[i + 1].Status == status)
            {
                _blocks[i] = _blocks[i] with { Size = _blocks[i].Size + _blocks[i + 1].Size };
                _blocks.RemoveAt(i + 1);
            }
            if (i > 0 && _blocks[i - 1].Status == status)
            {
                _blocks[i - 1] = _blocks[i - 1] with { Size = _blocks[i - 1].Size + _blocks[i].Size };
                _blocks.RemoveAt(i);
            }
        }
    }

    void SplitAt(long pos)
    {
        if (pos <= 0 || pos >= Size) return;
        int i = FindIndex(pos);
        var b = _blocks[i];
        if (b.Pos == pos) return;
        _blocks[i] = b with { Size = pos - b.Pos };
        _blocks.Insert(i + 1, new Block(pos, b.End - pos, b.Status));
    }

    int FindIndex(long pos)
    {
        int lo = 0, hi = _blocks.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            var b = _blocks[mid];
            if (pos < b.Pos) hi = mid - 1;
            else if (pos >= b.End) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    // ---- 検索 ----

    /// <summary>from 以降で最初に status を持つ範囲（開始位置は from 以上に切り詰め）</summary>
    public Block? FindNext(long from, BlockStatus status)
    {
        lock (_lock)
        {
            if (from >= Size) return null;
            int i = FindIndex(Math.Max(from, 0));
            if (i < 0) return null;
            for (; i < _blocks.Count; i++)
            {
                var b = _blocks[i];
                if (b.Status != status) continue;
                long p = Math.Max(b.Pos, from);
                return new Block(p, b.End - p, status);
            }
            return null;
        }
    }

    /// <summary>before より前で最後に status を持つ範囲（終了位置は before 以下に切り詰め）</summary>
    public Block? FindPrev(long before, BlockStatus status)
    {
        lock (_lock)
        {
            if (before <= 0) return null;
            int i = FindIndex(Math.Min(before, Size) - 1);
            if (i < 0) return null;
            for (; i >= 0; i--)
            {
                var b = _blocks[i];
                if (b.Status != status) continue;
                long e = Math.Min(b.End, before);
                return new Block(b.Pos, e - b.Pos, status);
            }
            return null;
        }
    }

    public long FirstFinishedPos()
    {
        lock (_lock)
        {
            foreach (var b in _blocks) if (b.Status == BlockStatus.Finished) return b.Pos;
            return 0;
        }
    }

    public List<Block> Snapshot()
    {
        lock (_lock) return new List<Block>(_blocks);
    }

    public MapTotals Totals()
    {
        long nt = 0, ntr = 0, ns = 0, bad = 0, fin = 0; int areas = 0, count;
        lock (_lock)
        {
            count = _blocks.Count;
            foreach (var b in _blocks)
            {
                switch (b.Status)
                {
                    case BlockStatus.NonTried: nt += b.Size; break;
                    case BlockStatus.NonTrimmed: ntr += b.Size; break;
                    case BlockStatus.NonScraped: ns += b.Size; break;
                    case BlockStatus.BadSector: bad += b.Size; areas++; break;
                    case BlockStatus.Finished: fin += b.Size; break;
                }
            }
        }
        return new MapTotals(nt, ntr, ns, bad, fin, areas, count);
    }

    // ---- 入出力 ----

    public void Save(string path, string commandLine, DateTime startTime)
    {
        var sb = new StringBuilder();
        sb.Append("# Mapfile. Created by StorageRescue (GNU ddrescue compatible format)\n");
        sb.Append("# Command line: ").Append(commandLine).Append('\n');
        sb.Append("# Start time:   ").Append(startTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("# Current time: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
        if (CurrentStatus == RescuePhase.Finished) sb.Append("# Finished\n");
        sb.Append("# current_pos  current_status  current_pass\n");
        lock (_lock)
        {
            sb.Append(Hex(CurrentPos)).Append("     ").Append((char)CurrentStatus).Append("               ")
              .Append(CurrentPass.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("#      pos        size  status\n");
            foreach (var b in _blocks)
                sb.Append(Hex(b.Pos)).Append("  ").Append(Hex(b.Size)).Append("  ").Append((char)b.Status).Append('\n');
        }

        // 一時ファイルに書いてから置き換える（書き込み途中の電源断でも mapfile を壊さない）
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = Encoding.ASCII.GetBytes(sb.ToString());
            fs.Write(bytes);
            fs.Flush(true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    static string Hex(long v) => "0x" + v.ToString("X8", CultureInfo.InvariantCulture);

    public static Mapfile Load(string path, long size)
    {
        var map = new Mapfile(size);
        bool statusRead = false;
        long expected = 0;
        var loaded = new List<Block>();
        int lineNo = 0;
        foreach (var raw in File.ReadLines(path))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (!statusRead)
            {
                if (t.Length < 2) throw new FormatException($"mapfile {lineNo}行目: status 行が不正です");
                map.CurrentPos = ParseNum(t[0]);
                map.CurrentStatus = t[1][0] switch
                {
                    '?' => RescuePhase.Copying, '*' => RescuePhase.Trimming, '%' => RescuePhase.Sweeping,
                    '/' => RescuePhase.Scraping, '-' => RescuePhase.Retrying, '+' => RescuePhase.Finished,
                    _ => RescuePhase.Copying,
                };
                map.CurrentPass = t.Length >= 3 ? int.Parse(t[2], CultureInfo.InvariantCulture) : 1;
                statusRead = true;
                continue;
            }
            if (t.Length < 3) throw new FormatException($"mapfile {lineNo}行目: ブロック行が不正です");
            long pos = ParseNum(t[0]), sz = ParseNum(t[1]);
            var st = t[2][0] switch
            {
                '?' => BlockStatus.NonTried, '*' => BlockStatus.NonTrimmed, '/' => BlockStatus.NonScraped,
                '-' => BlockStatus.BadSector, '+' => BlockStatus.Finished,
                _ => throw new FormatException($"mapfile {lineNo}行目: 未知の状態文字 '{t[2]}'"),
            };
            if (pos != expected) throw new FormatException($"mapfile {lineNo}行目: ブロックが連続していません");
            if (pos + sz > size) throw new FormatException("mapfile がディスク容量より大きい範囲を含んでいます（別ディスクの mapfile では？）");
            loaded.Add(new Block(pos, sz, st));
            expected = pos + sz;
        }
        if (expected < size) loaded.Add(new Block(expected, size - expected, BlockStatus.NonTried));
        map._blocks.Clear();
        foreach (var b in loaded)
        {
            if (b.Size <= 0) continue;
            int last = map._blocks.Count - 1;
            if (last >= 0 && map._blocks[last].Status == b.Status)
                map._blocks[last] = map._blocks[last] with { Size = map._blocks[last].Size + b.Size };
            else
                map._blocks.Add(b);
        }
        return map;
    }

    static long ParseNum(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : long.Parse(s, CultureInfo.InvariantCulture);
}

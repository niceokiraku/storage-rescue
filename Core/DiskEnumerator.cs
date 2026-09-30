using System.Runtime.InteropServices;
using System.Text;
using static StorageRescue.Core.Native;

namespace StorageRescue.Core;

public sealed record DiskInfo(int Number, string Model, string Serial, string BusType, long Size, int SectorSize, bool Offline)
{
    /// <summary>再接続後に PhysicalDrive 番号が変わっても同じディスクか判定する</summary>
    public bool IsSameDisk(DiskInfo other) =>
        Model == other.Model && Serial == other.Serial && Size == other.Size;

    public override string ToString() =>
        $"PhysicalDrive{Number}: {Model}  S/N:{(Serial.Length > 0 ? Serial : "(なし)")}  {Units.Bytes(Size)}  [{BusType}]{(Offline ? "  [オフライン]" : "")}";
}

public static unsafe class DiskEnumerator
{
    const int MaxDrives = 64;

    /// <summary>
    /// アクセス権 0 でディスクを開き、モデル・シリアル・容量を取得する。
    /// 容量が取れない（メディアなし・応答なし）場合は Size=0 を返す。
    /// </summary>
    public static DiskInfo? Query(int number)
    {
        using var h = CreateFileW($@"\\.\PhysicalDrive{number}", 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;

        string model = "", serial = "", bus = "?";
        var query = stackalloc byte[12]; // STORAGE_PROPERTY_QUERY: PropertyId=0(StorageDeviceProperty), QueryType=0
        new Span<byte>(query, 12).Clear();
        var desc = stackalloc byte[1024];
        if (DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, query, 12, desc, 1024, out int ret, IntPtr.Zero) && ret >= 36)
        {
            string Str(int offsetField)
            {
                int off = *(int*)(desc + offsetField);
                if (off <= 0 || off >= ret) return "";
                int len = 0;
                while (off + len < ret && desc[off + len] != 0) len++;
                return Encoding.ASCII.GetString(desc + off, len).Trim();
            }
            string vendor = Str(12), product = Str(16);
            model = string.Join(' ', new[] { vendor, product }.Where(s => s.Length > 0));
            serial = Str(24);
            bus = *(int*)(desc + 28) switch
            {
                1 => "SCSI", 3 => "ATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 11 => "SATA",
                12 => "SD", 13 => "MMC", 14 => "Virtual", 15 => "VHD", 16 => "Spaces", 17 => "NVMe",
                var v => $"Bus{v}",
            };
        }

        long size = 0; int sector = 512;
        var geo = stackalloc byte[256];
        if (DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, 0, geo, 256, out ret, IntPtr.Zero) && ret >= 32)
        {
            sector = *(int*)(geo + 20); // DISK_GEOMETRY.BytesPerSector
            size = *(long*)(geo + 24);  // DiskSize
            if (sector <= 0) sector = 512;
        }

        bool offline = false;
        var attr = stackalloc byte[16]; // GET_DISK_ATTRIBUTES
        if (DeviceIoControl(h, IOCTL_DISK_GET_DISK_ATTRIBUTES, null, 0, attr, 16, out _, IntPtr.Zero))
            offline = (*(long*)(attr + 8) & DISK_ATTRIBUTE_OFFLINE) != 0;
        return new DiskInfo(number, model, serial, bus, size, sector, offline);
    }

    /// <summary>全ディスクを並列に問い合わせる。応答しないディスクは timeout で見捨てる。</summary>
    public static List<DiskInfo> EnumerateAll(int timeoutMs = 4000)
    {
        var tasks = Enumerable.Range(0, MaxDrives)
            .Select(n => Task.Factory.StartNew(() => Query(n), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();
        Task.WaitAll(tasks, timeoutMs);
        return tasks.Where(t => t.IsCompletedSuccessfully && t.Result != null)
                    .Select(t => t.Result!).OrderBy(d => d.Number).ToList();
    }

    public static DiskInfo? FindSameDisk(DiskInfo target) =>
        EnumerateAll().FirstOrDefault(d => d.IsSameDisk(target));

    /// <summary>パスが置かれているボリュームのディスク番号（取得できなければ null）</summary>
    public static int? GetDiskNumberForPath(string path)
    {
        var buf = stackalloc char[512];
        if (!GetVolumePathNameW(Path.GetFullPath(path), buf, 512)) return null;
        string vol = new string(buf).TrimEnd('\\');
        if (vol.Length != 2 || vol[1] != ':') return null; // ドライブ文字以外（マウントポイント等）は未対応
        using var h = CreateFileW($@"\\.\{vol}", 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var num = stackalloc byte[12]; // STORAGE_DEVICE_NUMBER
        if (!DeviceIoControl(h, IOCTL_STORAGE_GET_DEVICE_NUMBER, null, 0, num, 12, out _, IntPtr.Zero)) return null;
        return *(int*)(num + 4);
    }

    const long DISK_ATTRIBUTE_OFFLINE = 1, DISK_ATTRIBUTE_READ_ONLY = 2;

    /// <summary>
    /// ディスクをオフライン＋読み取り専用にする（offline=false なら オンライン＋書き込み可 に戻す）。
    /// persist=true だと設定がディスクごとに保存され、電源の入れ直し・再接続・再起動後も維持される
    /// （diskpart の "offline disk" / "attributes disk set readonly" と同じ仕組み）。
    /// オフラインのディスクには Windows がボリュームを作らないため、ドライブ文字も付かずマウントもされない。
    /// </summary>
    public static bool TrySetOffline(int number, bool offline, bool persist, out string error)
    {
        error = "";
        using var h = CreateFileW($@"\\.\PhysicalDrive{number}", GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) { error = $"open error {Marshal.GetLastWin32Error()}"; return false; }
        var a = stackalloc byte[40]; // SET_DISK_ATTRIBUTES
        new Span<byte>(a, 40).Clear();
        *(int*)a = 40;                                   // Version = sizeof
        a[4] = (byte)(persist ? 1 : 0);                  // Persist
        *(long*)(a + 8) = offline ? DISK_ATTRIBUTE_OFFLINE | DISK_ATTRIBUTE_READ_ONLY : 0;
        *(long*)(a + 16) = DISK_ATTRIBUTE_OFFLINE | DISK_ATTRIBUTE_READ_ONLY; // AttributesMask
        if (!DeviceIoControl(h, IOCTL_DISK_SET_DISK_ATTRIBUTES, a, 40, null, 0, out _, IntPtr.Zero))
        {
            error = $"IOCTL error {Marshal.GetLastWin32Error()}";
            return false;
        }
        return true;
    }

    /// <summary>現在オフラインかどうか（取得できなければ null）</summary>
    public static bool? IsOffline(int number) => Query(number)?.Offline;

    /// <summary>デバイスマネージャーの「ハードウェア変更のスキャン」相当</summary>
    public static bool RescanHardware()
    {
        if (CM_Locate_DevNodeW(out uint root, null, 0) != 0) return false;
        return CM_Reenumerate_DevNode(root, 0) == 0;
    }
}

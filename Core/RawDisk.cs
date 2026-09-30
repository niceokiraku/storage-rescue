using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static StorageRescue.Core.Native;

namespace StorageRescue.Core;

public enum ReadStatus { Ok, Error, Timeout, Hung }

public readonly record struct ReadResult(ReadStatus Status, int Bytes, int Win32Error);

/// <summary>
/// \\.\PhysicalDriveN を FILE_FLAG_OVERLAPPED で開き、タイムアウト付きで読み取る。
/// -d 指定時は FILE_FLAG_NO_BUFFERING を付ける（ddrescue の -d / --idirect 相当）。
/// タイムアウトしたら CancelIoEx し、それでも戻らない I/O は「Hung」として
/// ハンドル・OVERLAPPED・バッファを放棄する（カーネルが後から書き込む可能性があるため解放しない）。
/// </summary>
public sealed unsafe class RawDisk : IDisposable
{
    const int CancelWaitMs = 5000;

    readonly SafeFileHandle _h;
    readonly OVERLAPPED* _ov;
    readonly IntPtr _event;
    bool _disposed;

    public int Number { get; }
    public bool Poisoned { get; private set; }

    RawDisk(int number, SafeFileHandle h)
    {
        Number = number;
        _h = h;
        _ov = (OVERLAPPED*)NativeMemory.AllocZeroed((nuint)sizeof(OVERLAPPED));
        _event = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        if (_event == IntPtr.Zero) throw new Win32Exception();
    }

    /// <param name="direct">ddrescue の -d 相当。true なら FILE_FLAG_NO_BUFFERING でキャッシュを経由しない</param>
    public static RawDisk Open(int number, bool direct)
    {
        uint flags = FILE_FLAG_OVERLAPPED | (direct ? FILE_FLAG_NO_BUFFERING : 0);
        var h = CreateFileW($@"\\.\PhysicalDrive{number}", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, flags, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new RawDisk(number, h);
    }

    /// <param name="buffer">セクタサイズ境界（4096推奨）に整列したバッファ</param>
    public ReadResult Read(long offset, byte* buffer, int length, int timeoutMs)
    {
        if (Poisoned || _disposed) throw new InvalidOperationException("disk handle is not usable");

        *_ov = default;
        _ov->OffsetLow = (uint)offset;
        _ov->OffsetHigh = (uint)(offset >> 32);
        _ov->hEvent = _event;
        ResetEvent(_event);

        if (ReadFile(_h, buffer, length, IntPtr.Zero, _ov)) return Complete(length);

        int err = Marshal.GetLastWin32Error();
        if (err != ERROR_IO_PENDING) return new ReadResult(ReadStatus.Error, 0, err);

        if (WaitForSingleObject(_event, (uint)timeoutMs) == WAIT_OBJECT_0) return Complete(length);

        // タイムアウト → キャンセル要求
        CancelIoEx(_h, _ov);
        if (WaitForSingleObject(_event, CancelWaitMs) == WAIT_OBJECT_0)
        {
            var r = Complete(length);
            // キャンセルより先に完了していた場合はそのまま成功扱い
            return r.Status == ReadStatus.Ok ? r : new ReadResult(ReadStatus.Timeout, 0, r.Win32Error);
        }

        // キャンセルにも応答しない：ドライバ内で固まっている
        Poisoned = true;
        return new ReadResult(ReadStatus.Hung, 0, 0);
    }

    ReadResult Complete(int length)
    {
        if (!GetOverlappedResult(_h, _ov, out int n, false))
            return new ReadResult(ReadStatus.Error, 0, Marshal.GetLastWin32Error());
        if (n != length) return new ReadResult(ReadStatus.Error, n, -1); // 短い読み取り
        return new ReadResult(ReadStatus.Ok, n, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Poisoned)
        {
            // 保留中の I/O が OVERLAPPED/イベントを参照し続けるため解放しない（意図的なリーク）。
            // CloseHandle 自体がブロックする可能性に備え、別スレッドで閉じる。
            var h = _h;
            new Thread(() => { try { h.Dispose(); } catch { } }) { IsBackground = true }.Start();
            return;
        }
        _h.Dispose();
        CloseHandle(_event);
        NativeMemory.Free(_ov);
    }
}

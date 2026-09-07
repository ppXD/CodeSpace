using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>Kernel file identity for continuity observations only. Linux uses the fixed statx UAPI; macOS uses the SDK's 64-bit inode ABI. No mtime, random marker or shell utility stands in for identity.</summary>
internal readonly record struct LocalAcceptanceFileIdentity(ulong Device, ulong Inode)
{
    public static SafeFileHandle Open(string path, bool directory)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Local acceptance continuity is supported on Linux and macOS.");
        // Nonblocking excludes FIFO-open hangs; no-follow rejects a replaced leaf. System ancestors such as macOS
        // /var may be symlinks. Relative oracle components are separately checked, with the documented race bound.
        var flags = OperatingSystem.IsMacOS() ? 0x01000104 : 0x000A0800; // CLOEXEC | NOFOLLOW | NONBLOCK
        if (directory) flags |= OperatingSystem.IsMacOS() ? 0x00100000 : 0x00010000;
        var descriptor = OpenNative(path, flags);
        if (descriptor < 0) throw NativeFailure();
        var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        try
        {
            var (_, mode) = ReadStatus(handle);
            if ((mode & 0xF000) != (directory ? 0x4000 : 0x8000)) throw new IOException("Local acceptance requires a regular file or directory, never a special file.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static LocalAcceptanceFileIdentity Read(SafeFileHandle handle) => ReadStatus(handle).Identity;

    private static (LocalAcceptanceFileIdentity Identity, ushort Mode) ReadStatus(SafeFileHandle handle)
    {
        if (OperatingSystem.IsMacOS())
        {
            MacStatus status;
            var result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? StatMac64(handle, out status) : StatMac(handle, out status);
            if (result != 0) throw NativeFailure();
            return (new(status.Device, status.Inode), status.Mode);
        }
        if (StatLinux(handle, "", 0x1000, 0x101, out var linux) != 0) throw NativeFailure(); // AT_EMPTY_PATH; TYPE | INO
        if ((linux.Mask & 0x101) != 0x101) throw new PlatformNotSupportedException("The filesystem did not provide the requested directory identity.");
        return (new(((ulong)linux.DeviceMajor << 32) | linux.DeviceMinor, linux.Inode), linux.Mode);
    }

    private static IOException NativeFailure() => new($"Local acceptance filesystem observation failed: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}.");

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatus
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct MacStatus
    {
        [FieldOffset(0)] public uint Device;
        [FieldOffset(4)] public ushort Mode;
        [FieldOffset(8)] public ulong Inode;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int StatLinux(SafeFileHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out LinuxStatus status);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int StatMac(SafeFileHandle handle, out MacStatus status);
    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int StatMac64(SafeFileHandle handle, out MacStatus status);
}

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;

/// <summary>
/// Publishes a completed, closed staging file without replacing an existing object. Unix File.Move(false) may
/// use an existence check followed by rename, so it is not the create-only primitive. A hard link installs the
/// whole inode atomically or fails; unsupported filesystems must not fall back to copying visible partial bytes.
/// This is process-crash atomicity, not a power-loss durability claim: file and directory fsync are separate.
/// </summary>
internal static class LocalRwxAtomicFilePublication
{
    private const int UnixAlreadyExists = 17;
    private const int UnixCrossDevice = 18;
    private const int UnixTooManyLinks = 31;
    private const int UnixPermissionDenied = 13;
    private const int UnixOperationNotPermitted = 1;

    public static ArtifactStorageError? CreateOnly(string stagingPath, string destinationPath)
    {
        if (OperatingSystem.IsWindows())
        {
            try { File.Move(stagingPath, destinationPath, overwrite: false); }
            catch (IOException) when (File.Exists(destinationPath)) { return AlreadyExists(); }
            return null;
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return new ArtifactStorageError(ArtifactStorageErrorCode.Unsupported, "Atomic local create-only publication is supported on Linux, macOS and Windows.");

        if (Link(stagingPath, destinationPath) == 0) return null;
        return FromUnixError(Marshal.GetLastPInvokeError(), OperatingSystem.IsMacOS());
    }

    internal static ArtifactStorageError FromUnixError(int error, bool macOS)
    {
        if (error == UnixAlreadyExists) return AlreadyExists();

        var code = error switch
        {
            UnixCrossDevice or UnixTooManyLinks => ArtifactStorageErrorCode.Unsupported,
            UnixPermissionDenied or UnixOperationNotPermitted => ArtifactStorageErrorCode.Forbidden,
            _ when error == (macOS ? 45 : 95) || error == (macOS ? 78 : 38) => ArtifactStorageErrorCode.Unsupported,
            _ => ArtifactStorageErrorCode.ProviderFailure,
        };
        var message = $"Atomic local create-only publication failed: {new Win32Exception(error).Message}. No copy fallback was attempted.";
        return new ArtifactStorageError(code, message, IsRetryable: code == ArtifactStorageErrorCode.ProviderFailure, ProviderCode: $"errno:{error}");
    }

    private static ArtifactStorageError AlreadyExists() => new(ArtifactStorageErrorCode.AlreadyExists, "The destination object already exists.");

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
}

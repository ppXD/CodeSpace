using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodeSpace.Messages.Agents;

namespace CodeSpace.NativeLaunch;

internal static class NativeLaunchFiles
{
    public static string DirectoryFor(string spoolDirectory) => Path.Combine(spoolDirectory, NativeLaunchProtocol.DirectoryName);
    public static string PathFor(string directory, string file) => Path.Combine(directory, file);

    public static T Read<T>(string directory, string file)
    {
        using var stream = new FileStream(PathFor(directory, file), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 64 * 1024) throw new IOException("Native launch metadata exceeds its bound.");
        return JsonSerializer.Deserialize<T>(stream, NativeLaunchProtocol.Json) ?? throw new InvalidDataException("Native launch metadata is empty.");
    }

    public static bool TryCreate<T>(string directory, string file, T value)
    {
        var destination = PathFor(directory, file);
        var temporary = destination + ".publishing-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(stream, value, NativeLaunchProtocol.Json);
                stream.Flush(flushToDisk: true);
            }
            return PublishCreateOnly(temporary, destination);
        }
        finally { File.Delete(temporary); }
    }

    private static bool PublishCreateOnly(string temporary, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            try { File.Move(temporary, destination, overwrite: false); return true; }
            catch (IOException) when (File.Exists(destination)) { return false; }
        }
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Atomic native metadata publication is unavailable on this platform.");
        // Same-directory hard-link publication installs the completed inode or reports EEXIST atomically.
        // Do not replace this with an existence check followed by rename, or a visible copy fallback.
        // Only the winner has acquired a start commitment. File flush is not directory/power-loss durability.
        if (Link(temporary, destination) == 0) return true;
        var error = Marshal.GetLastPInvokeError();
        if (error == 17) return false; // EEXIST on both supported Unix platforms.
        throw new IOException("Atomic native metadata publication failed; no fallback was attempted.", new Win32Exception(error));
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

    public static void Replace<T>(string directory, string file, T value)
    {
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (!TryCreate(directory, temporary, value)) throw new IOException("Native launch metadata temporary collision.");
            File.Move(PathFor(directory, temporary), PathFor(directory, file), overwrite: true);
        }
        finally { File.Delete(PathFor(directory, temporary)); }
    }

    public static async Task WriteFrameAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, NativeLaunchProtocol.Json);
        if (bytes.Length > NativeLaunchProtocol.MaximumFrameBytes) throw new InvalidDataException("Native invocation exceeds its pipe bound.");
        var size = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, bytes.Length);
        await stream.WriteAsync(size, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadFrameAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var size = new byte[4];
        await stream.ReadExactlyAsync(size, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(size);
        if (length is <= 0 or > NativeLaunchProtocol.MaximumFrameBytes) throw new InvalidDataException("Invalid native invocation frame length.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, NativeLaunchProtocol.Json) ?? throw new InvalidDataException("Empty native invocation frame.");
    }
}

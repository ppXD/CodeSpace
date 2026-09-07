using System.Buffers.Binary;
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
        var path = PathFor(directory, file);
        FileStream stream;
        try { stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
        catch (IOException) when (File.Exists(path)) { return false; }
        using (stream)
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            JsonSerializer.Serialize(stream, value, NativeLaunchProtocol.Json);
            stream.Flush(flushToDisk: true);
        }
        return true;
    }

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

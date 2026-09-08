using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;

/// <summary>
/// Gives each v2 staging alias a durable, exclusively-owned recovery record. Recovery never guesses ownership from
/// age: a record is eligible only when the writer's cross-process operating-system lock can be acquired.
/// The configured root is trusted operator infrastructure and must not contain tenant-created symlinks.
/// </summary>
internal sealed class LocalRwxStagingLease : IDisposable
{
    private const int SchemaVersion = 1;
    private readonly FileStream _ownership;
    private bool _disposed;

    private LocalRwxStagingLease(string stagingPath, string leasePath, FileStream ownership)
    {
        StagingPath = stagingPath;
        LeasePath = leasePath;
        _ownership = ownership;
    }

    public string StagingPath { get; }
    public string LeasePath { get; }

    public static LocalRwxStagingLease Create(string root, string destinationPath)
    {
        var id = Guid.NewGuid();
        var stagingPath = destinationPath + ".upload-v2-" + id.ToString("N");
        var leaseDirectory = LocalRwxStagingLeaseRecovery.LeaseDirectory(root);
        Directory.CreateDirectory(leaseDirectory);
        var leasePath = Path.Combine(leaseDirectory, id.ToString("N") + ".json");
        var ownership = new FileStream(leasePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Delete);
        try
        {
            if (!LocalRwxLeaseLock.TryAcquire(ownership)) throw new IOException("A newly-created staging lease was unexpectedly held by another process.");
            if (!File.Exists(leasePath)) throw new IOException("The staging lease lost its active directory entry before ownership was established.");
            var record = new LeaseRecord(SchemaVersion, Path.GetRelativePath(root, stagingPath).Replace('\\', '/'));
            JsonSerializer.Serialize(ownership, record);
            ownership.Flush(flushToDisk: true);
            ownership.Position = 0;
            return new LocalRwxStagingLease(stagingPath, leasePath, ownership);
        }
        catch
        {
            ownership.Dispose();
            TryDelete(leasePath);
            throw;
        }
    }

    public void TryCleanup()
    {
        if (_disposed) return;
        TryDelete(StagingPath);
        if (!File.Exists(StagingPath)) TryDelete(LeasePath);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ownership.Dispose();
    }

    internal sealed record LeaseRecord([property: JsonPropertyName("schemaVersion")] int SchemaVersion, [property: JsonPropertyName("stagingPath")] string StagingPath);

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

internal static class LocalRwxStagingLeaseRecovery
{
    private const int MaximumRecordsPerSweep = 128;
    private const int MaximumRecordBytes = 4096;

    public static string LeaseDirectory(string root) => Path.Combine(root, ".codespace", "staging-leases", "v1");

    public static void Sweep(string root)
    {
        var directory = LeaseDirectory(root);
        if (!Directory.Exists(directory)) return;

        try
        {
            foreach (var leasePath in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Take(MaximumRecordsPerSweep)) RecoverOne(root, leasePath);
        }
        catch
        {
            // Maintenance is best effort. A failed sweep cannot make an otherwise valid put or health probe fail.
        }
    }

    private static void RecoverOne(string root, string leasePath)
    {
        try
        {
            using var ownership = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
            if (!LocalRwxLeaseLock.TryAcquire(ownership)) return;
            if (!TryReadRecord(ownership, out var record) || !TryResolveStagingPath(root, leasePath, record!, out var stagingPath))
            {
                Quarantine(root, leasePath);
                return;
            }

            LocalRwxStagingLease.TryDelete(stagingPath);
            if (!File.Exists(stagingPath)) LocalRwxStagingLease.TryDelete(leasePath);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool TryReadRecord(FileStream stream, out LocalRwxStagingLease.LeaseRecord? record)
    {
        record = null;
        if (stream.Length is <= 0 or > MaximumRecordBytes) return false;
        try
        {
            stream.Position = 0;
            record = JsonSerializer.Deserialize<LocalRwxStagingLease.LeaseRecord>(stream);
            return record is { SchemaVersion: 1 } && !string.IsNullOrWhiteSpace(record.StagingPath);
        }
        catch (JsonException) { return false; }
    }

    private static bool TryResolveStagingPath(string root, string leasePath, LocalRwxStagingLease.LeaseRecord record, out string stagingPath)
    {
        stagingPath = string.Empty;
        var idText = Path.GetFileNameWithoutExtension(leasePath);
        if (!Guid.TryParseExact(idText, "N", out var id) || Path.IsPathRooted(record.StagingPath) || record.StagingPath.IndexOf('\0') >= 0) return false;
        var segments = record.StagingPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or "..")) return false;

        stagingPath = Path.GetFullPath(Path.Combine([root, .. segments]));
        var objectRoot = Path.GetFullPath(Path.Combine(root, "objects")) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return stagingPath.StartsWith(objectRoot, comparison) && stagingPath.EndsWith(".upload-v2-" + id.ToString("N"), comparison);
    }

    private static void Quarantine(string root, string leasePath)
    {
        try
        {
            var directory = Path.Combine(root, ".codespace", "staging-lease-quarantine", "v1");
            Directory.CreateDirectory(directory);
            File.Move(leasePath, Path.Combine(directory, Path.GetFileName(leasePath)), overwrite: false);
        }
        catch { }
    }
}

internal static class LocalRwxLeaseLock
{
    private const int Exclusive = 2;
    private const int NonBlocking = 4;
    private const int LinuxWouldBlock = 11;
    private const int MacOsWouldBlock = 35;

    public static bool TryAcquire(FileStream stream)
    {
        if (OperatingSystem.IsWindows()) return true; // FileShare.Delete denies the recoverer's read/write open.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Local RWX staging leases require Linux, macOS or Windows.");
        if (Flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), Exclusive | NonBlocking) == 0) return true;
        var error = Marshal.GetLastPInvokeError();
        if (error is LinuxWouldBlock or MacOsWouldBlock) return false;
        throw new IOException($"Could not acquire the local RWX staging lease (errno {error}).");
    }

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fileDescriptor, int operation);
}

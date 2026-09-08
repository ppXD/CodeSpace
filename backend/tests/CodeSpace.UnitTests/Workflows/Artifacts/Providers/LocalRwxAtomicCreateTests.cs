using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Providers;

[Trait("Category", "Unit")]
public sealed class LocalRwxAtomicCreateTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codespace-atomic-create-tests", Guid.NewGuid().ToString("N"));
    private readonly List<Process> _children = [];

    [Fact]
    public async Task A_killed_legacy_lock_owner_cannot_permanently_block_a_new_create()
    {
        const string key = "crash/legacy-lock";
        var child = StartWorker(key, "legacy-lock", 1);
        (await ReadLineAsync(child)).ShouldBe("locked");
        await KillAsync(child);
        var lockPath = Path.Combine(_root, ".codespace", "create-locks", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
        File.Exists(lockPath).ShouldBeTrue("SIGKILL must leave the old writer's fixed lock on disk");

        var result = await PutAsync(key, 2);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        (await File.ReadAllBytesAsync(ObjectPath(key))).ShouldBe(Payload(2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Different_processes_racing_one_key_publish_one_complete_winner_without_overwrite(int round)
    {
        var key = $"race/shared-key-{round}";
        var children = Enumerable.Range(1, 8).Select(value => StartWorker(key, "staged", (byte)value)).ToArray();
        foreach (var child in children) (await ReadLineAsync(child)).ShouldBe("ready");
        foreach (var child in children) await ContinueAsync(child);
        foreach (var child in children) (await ReadLineAsync(child)).ShouldBe("staged");
        foreach (var child in children) await ContinueAsync(child);
        var outcomes = await Task.WhenAll(children.Select(ReadLineAsync));
        foreach (var child in children) await AssertExitedAsync(child);

        outcomes.Count(value => value == "stored").ShouldBe(1);
        outcomes.Count(value => value == nameof(ArtifactStorageErrorCode.AlreadyExists)).ShouldBe(7);
        var winner = Array.IndexOf(outcomes, "stored") + 1;
        (await File.ReadAllBytesAsync(ObjectPath(key))).ShouldBe(Payload((byte)winner));
        Directory.GetFiles(Path.GetDirectoryName(ObjectPath(key))!, "*.upload-*").ShouldBeEmpty();
    }

    [Fact]
    public async Task Killing_a_writer_after_staging_does_not_publish_partial_bytes_or_block_the_retry()
    {
        const string key = "crash/staged";
        var child = StartWorker(key, "staged", 3);
        (await ReadLineAsync(child)).ShouldBe("ready");
        await ContinueAsync(child);
        (await ReadLineAsync(child)).ShouldBe("staged");
        File.Exists(ObjectPath(key)).ShouldBeFalse();
        Directory.GetFiles(Path.GetDirectoryName(ObjectPath(key))!, "*.upload-v2-*").Length.ShouldBe(1);
        LeaseFiles().Length.ShouldBe(1, "the recoverer needs a durable owner record before staging becomes visible");
        await KillAsync(child);

        var retry = await PutAsync(key, 4);

        retry.IsSuccess.ShouldBeTrue(retry.Error?.Message);
        (await File.ReadAllBytesAsync(ObjectPath(key))).ShouldBe(Payload(4));
        Directory.GetFiles(Path.GetDirectoryName(ObjectPath(key))!, "*.upload-v2-*").ShouldBeEmpty("a dead writer's alias must be reclaimed without an age guess");
        LeaseFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_live_writer_lease_prevents_a_concurrent_probe_from_reclaiming_its_staging_bytes()
    {
        const string key = "recovery/live";
        var child = StartWorker(key, "staged", 13);
        (await ReadLineAsync(child)).ShouldBe("ready");
        await ContinueAsync(child);
        (await ReadLineAsync(child)).ShouldBe("staged");
        var staging = Directory.GetFiles(Path.GetDirectoryName(ObjectPath(key))!, "*.upload-v2-*").Single();
        LeaseFiles().Length.ShouldBe(1);

        await using (var driver = new LocalRwxArtifactStorageDriver(_root))
            (await driver.ProbeAsync(new ArtifactStorageProbeRequest(), CancellationToken.None)).Status.ShouldBe(ArtifactStorageProbeStatus.Available);

        File.Exists(staging).ShouldBeTrue("a held cross-process lease is authoritative evidence that the writer is alive");
        LeaseFiles().Length.ShouldBe(1);
        await ContinueAsync(child);
        (await ReadLineAsync(child)).ShouldBe("stored");
        await AssertExitedAsync(child);
        (await File.ReadAllBytesAsync(ObjectPath(key))).ShouldBe(Payload(13));
        File.Exists(staging).ShouldBeFalse();
        LeaseFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task Legacy_unleased_aliases_are_never_age_guessed_by_the_v2_recoverer()
    {
        var directory = Path.GetDirectoryName(ObjectPath("legacy/target"))!;
        Directory.CreateDirectory(directory);
        var legacy = Path.Combine(directory, "target.upload-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(legacy, Payload(14));

        var result = await PutAsync("another/object", 15);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        File.Exists(legacy).ShouldBeTrue("an old writer can be paused in the unlocked pre-publication gap, so mtime is not ownership evidence");
    }

    [Fact]
    public async Task A_malformed_escape_lease_is_quarantined_without_touching_bytes_outside_the_object_root()
    {
        var outside = Path.Combine(_root, "outside.bin");
        await File.WriteAllBytesAsync(outside, Payload(16));
        var leaseDirectory = LeaseDirectory();
        Directory.CreateDirectory(leaseDirectory);
        var lease = Path.Combine(leaseDirectory, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(lease, "{\"schemaVersion\":1,\"stagingPath\":\"../outside.bin\"}");

        await using (var driver = new LocalRwxArtifactStorageDriver(_root))
            (await driver.ProbeAsync(new ArtifactStorageProbeRequest(), CancellationToken.None)).Status.ShouldBe(ArtifactStorageProbeStatus.Available);

        (await File.ReadAllBytesAsync(outside)).ShouldBe(Payload(16));
        File.Exists(lease).ShouldBeFalse("an unlocked malformed record must not permanently block the bounded recovery queue");
        Directory.GetFiles(Path.Combine(_root, ".codespace", "staging-lease-quarantine", "v1"), "*.json").Length.ShouldBe(1);
    }

    [UnixFact]
    public async Task Recovery_after_publication_removes_only_the_alias_and_preserves_the_committed_inode()
    {
        const string key = "recovery/committed";
        var destination = ObjectPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var id = Guid.NewGuid();
        var staging = destination + ".upload-v2-" + id.ToString("N");
        await File.WriteAllBytesAsync(staging, Payload(17));
        LocalRwxAtomicFilePublication.CreateOnly(staging, destination).ShouldBeNull();
        var leaseDirectory = LeaseDirectory();
        Directory.CreateDirectory(leaseDirectory);
        var lease = Path.Combine(leaseDirectory, id.ToString("N") + ".json");
        await File.WriteAllTextAsync(lease, $"{{\"schemaVersion\":1,\"stagingPath\":\"objects/{key}.upload-v2-{id:N}\"}}");

        await using (var driver = new LocalRwxArtifactStorageDriver(_root))
            (await driver.ProbeAsync(new ArtifactStorageProbeRequest(), CancellationToken.None)).Status.ShouldBe(ArtifactStorageProbeStatus.Available);

        (await File.ReadAllBytesAsync(destination)).ShouldBe(Payload(17));
        File.Exists(staging).ShouldBeFalse();
        File.Exists(lease).ShouldBeFalse();
    }

    [Fact]
    public async Task Each_recovery_sweep_is_bounded_and_repeated_sweeps_make_forward_progress()
    {
        for (var index = 0; index < 129; index++) await CreateUnlockedOrphanAsync($"bounded/{index}", (byte)index);

        await ProbeAsync();

        LeaseFiles().Length.ShouldBe(1, "one probe must cap filesystem mutation even after an orphan storm");
        Directory.GetFiles(Path.Combine(_root, "objects", "bounded"), "*.upload-v2-*").Length.ShouldBe(1);

        await ProbeAsync();

        LeaseFiles().ShouldBeEmpty();
        Directory.GetFiles(Path.Combine(_root, "objects", "bounded"), "*.upload-v2-*").ShouldBeEmpty();
    }

    [Fact]
    public async Task Killing_after_publication_before_the_result_leaves_the_original_object_for_an_idempotent_retry()
    {
        const string key = "crash/published";
        var child = StartWorker(key, "published", 5);
        (await ReadLineAsync(child)).ShouldBe("ready");
        await ContinueAsync(child);
        (await ReadLineAsync(child)).ShouldBe("published");
        await KillAsync(child);

        var retry = await PutAsync(key, 6);

        retry.IsSuccess.ShouldBeFalse();
        retry.Error!.Code.ShouldBe(ArtifactStorageErrorCode.AlreadyExists);
        (await File.ReadAllBytesAsync(ObjectPath(key))).ShouldBe(Payload(5));
    }

    [MacOsFact]
    public async Task A_real_unlink_denial_after_publication_preserves_success_and_leaves_a_harmless_staging_alias()
    {
        const string key = "cleanup/denied";
        var child = StartWorker(key, "staged", 7);
        (await ReadLineAsync(child)).ShouldBe("ready");
        await ContinueAsync(child);
        (await ReadLineAsync(child)).ShouldBe("staged");
        var staging = Directory.GetFiles(Path.GetDirectoryName(ObjectPath(key))!, "*.upload-v2-*").Single();
        LeaseFiles().Length.ShouldBe(1);
        await SetDeleteAclAsync(staging, deny: true);

        try
        {
            await ContinueAsync(child);
            (await ReadLineAsync(child)).ShouldBe("stored", "cleanup failure must not turn committed bytes into a failed publication");
            await AssertExitedAsync(child);
            File.Exists(staging).ShouldBeTrue("the OS, not a fake, refused staging cleanup");
            LeaseFiles().Length.ShouldBe(1, "failed alias cleanup must retain a retryable recovery record");
            (await File.ReadAllBytesAsync(ObjectPath(key))).ShouldBe(Payload(7));
            (await PutAsync(key, 8)).Error!.Code.ShouldBe(ArtifactStorageErrorCode.AlreadyExists);
            (await File.ReadAllBytesAsync(ObjectPath(key))).ShouldBe(Payload(7));
        }
        finally { await SetDeleteAclAsync(staging, deny: false); }

        await using (var driver = new LocalRwxArtifactStorageDriver(_root))
            (await driver.ProbeAsync(new ArtifactStorageProbeRequest(), CancellationToken.None)).Status.ShouldBe(ArtifactStorageProbeStatus.Available);
        File.Exists(staging).ShouldBeFalse();
        LeaseFiles().ShouldBeEmpty();
    }

    [LinuxFact]
    public void Cross_device_publication_is_typed_unsupported_and_never_copies_into_the_destination()
    {
        var destination = Path.Combine(_root, "must-not-be-copied");

        var failure = LocalRwxAtomicFilePublication.CreateOnly("/proc/version", destination);

        failure.ShouldNotBeNull();
        failure.Code.ShouldBe(ArtifactStorageErrorCode.Unsupported);
        failure.ProviderCode.ShouldBe("errno:18");
        failure.IsRetryable.ShouldBeFalse();
        File.Exists(destination).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false, 95)]
    [InlineData(false, 38)]
    [InlineData(true, 45)]
    [InlineData(true, 78)]
    public void A_filesystem_without_hard_links_fails_closed_without_a_copy_fallback(bool macOS, int error)
    {
        var failure = LocalRwxAtomicFilePublication.FromUnixError(error, macOS);

        failure.Code.ShouldBe(ArtifactStorageErrorCode.Unsupported);
        failure.IsRetryable.ShouldBeFalse();
        failure.ProviderCode.ShouldBe($"errno:{error}");
    }

    [Fact]
    public async Task Publication_preserves_unicode_paths_and_the_byte_stream_sha256()
    {
        const string key = "產物/данные/échantillon.bin";
        var result = await PutAsync(key, 9);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Metadata!.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Payload(9))));
        (await File.ReadAllBytesAsync(ObjectPath(key))).ShouldBe(Payload(9));
    }

    private static async Task SetDeleteAclAsync(string path, bool deny)
    {
        var start = new ProcessStartInfo("/bin/chmod") { UseShellExecute = false, RedirectStandardError = true };
        if (deny)
        {
            start.ArgumentList.Add("+a");
            start.ArgumentList.Add("everyone deny delete");
        }
        else start.ArgumentList.Add("-N");
        start.ArgumentList.Add(path);
        using var child = Process.Start(start)!;
        await AssertExitedAsync(child);
    }

    private Process StartWorker(string key, string mode, byte payload)
    {
        var worker = typeof(StorageTestWorker.StorageTestWorker).Assembly.Location;
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "CodeSpace.UnitTests.runtimeconfig.json"), "--depsfile", Path.Combine(AppContext.BaseDirectory, "CodeSpace.UnitTests.deps.json"), worker, _root, key, mode, payload.ToString() })
            start.ArgumentList.Add(argument);
        var child = Process.Start(start)!;
        _children.Add(child);
        return child;
    }

    private static async Task<string?> ReadLineAsync(Process child) => await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));

    private static async Task ContinueAsync(Process child)
    {
        await child.StandardInput.WriteLineAsync("continue");
        await child.StandardInput.FlushAsync();
    }

    private static async Task AssertExitedAsync(Process child)
    {
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        child.ExitCode.ShouldBe(0, await child.StandardError.ReadToEndAsync());
    }

    private static async Task KillAsync(Process child)
    {
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        child.ExitCode.ShouldNotBe(0);
    }

    private async Task<ArtifactStoragePutResult> PutAsync(string key, byte value)
    {
        await using var driver = new LocalRwxArtifactStorageDriver(_root);
        await using var source = new MemoryStream(Payload(value));
        return await driver.PutAsync(new ArtifactStoragePutRequest(key, source) { Condition = ArtifactStorageWriteCondition.CreateOnly }, CancellationToken.None);
    }

    private async Task ProbeAsync()
    {
        await using var driver = new LocalRwxArtifactStorageDriver(_root);
        (await driver.ProbeAsync(new ArtifactStorageProbeRequest(), CancellationToken.None)).Status.ShouldBe(ArtifactStorageProbeStatus.Available);
    }

    private async Task CreateUnlockedOrphanAsync(string key, byte value)
    {
        var id = Guid.NewGuid();
        var staging = ObjectPath(key) + ".upload-v2-" + id.ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
        await File.WriteAllBytesAsync(staging, [value]);
        Directory.CreateDirectory(LeaseDirectory());
        await File.WriteAllTextAsync(Path.Combine(LeaseDirectory(), id.ToString("N") + ".json"), $"{{\"schemaVersion\":1,\"stagingPath\":\"objects/{key}.upload-v2-{id:N}\"}}");
    }

    private string ObjectPath(string key) => Path.Combine(_root, "objects", key);
    private string LeaseDirectory() => Path.Combine(_root, ".codespace", "staging-leases", "v1");
    private string[] LeaseFiles() => Directory.Exists(LeaseDirectory()) ? Directory.GetFiles(LeaseDirectory(), "*.json") : [];

    private static byte[] Payload(byte value) => Enumerable.Repeat(value, 1024 * 1024).ToArray();

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var child in _children)
        {
            if (!child.HasExited) await KillAsync(child);
            child.Dispose();
        }
        Directory.Delete(_root, recursive: true);
    }

    private sealed class MacOsFactAttribute : FactAttribute
    {
        public MacOsFactAttribute()
        {
            if (!OperatingSystem.IsMacOS()) Skip = "The real delete-denial injection uses macOS file ACLs.";
        }
    }

    private sealed class LinuxFactAttribute : FactAttribute
    {
        public LinuxFactAttribute()
        {
            if (!OperatingSystem.IsLinux()) Skip = "The real cross-device link injection requires Linux procfs.";
        }
    }

    private sealed class UnixFactAttribute : FactAttribute
    {
        public UnixFactAttribute()
        {
            if (OperatingSystem.IsWindows()) Skip = "The committed alias is a hard link on Unix; Windows create-only moves the staging file.";
        }
    }
}

using System.Diagnostics;
using System.Globalization;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Settings;
using CodeSpace.StorageTestWorker;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The per-run /30 reservation across real worker PROCESSES sharing one reservation directory, in the state a
/// rolling deploy leaves it: two child processes run the production host allocator
/// (<see cref="EgressSubnetAllocator.Host"/>) under the same spool root and start reserving at the same moment, while
/// the directory still holds the <c>10-*.lease</c> files the allocator wrote when its pool was 10/8, one of them
/// locked by this process as a worker on the old build would hold it. Real files, real <c>flock</c>s between real
/// processes; no opener seam, no in-process second instance standing in for a second worker.
///
/// <para>The old files must be left exactly as they are. Reading one would let the old build and the new hand out the
/// same subnet by name, and deleting one lets a third process create a fresh inode for a /30 while a second still
/// holds the old one. Old and new names never meet, because the new pool lies wholly outside 10/8.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class EgressSubnetReservationCrossProcessTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>How many /30s each worker process reserves. Two workers' worth crosses a third-octet boundary of the pool.</summary>
    private const int PerWorker = 64;

    /// <summary>The old reservation this test keeps locked for the whole run, as a worker still on the old build would.</summary>
    private const string LockedByAnOldWorker = "10-1-1-0_30.lease";

    private readonly string _spool = Path.Combine(Path.GetTempPath(), "cs-egress-xproc-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _children = [];

    [Fact]
    public async Task Two_worker_processes_never_hand_out_the_same_30_and_leave_the_old_10_8_reservations_alone()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var directory = ReservationDirectory();
        var legacy = StageLegacyReservations(directory);

        using var heldByAnOldWorker = new FileStream(Path.Combine(directory, LockedByAnOldWorker), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var workers = new[] { StartWorker(), StartWorker() };

        foreach (var worker in workers) await ExpectAsync(worker, "ready", deadline.Token);
        foreach (var worker in workers) await SayAsync(worker, "go");

        var leases = await Task.WhenAll(workers.Select(worker => ReadLeasesAsync(worker, deadline.Token)));

        output.WriteLine($"worker A: {string.Join(' ', leases[0])}");
        output.WriteLine($"worker B: {string.Join(' ', leases[1])}");

        leases[0].Intersect(leases[1]).ShouldBeEmpty("a /30 one worker process holds must be refused to the other, which the kernel's lock alone decides here");
        leases.SelectMany(held => held).ShouldBe(BottomOfThePool(2 * PerWorker), ignoreOrder: true, "between them the two processes hold exactly the bottom of 198.19.64.0–198.19.191.255: every /30 went to one of them, none was passed over by both, and none came from 10/8");
        ReservationFilesIn(directory).ShouldBe(legacy.Keys.Concat(BottomOfThePool(2 * PerWorker).Select(FileNameFor)), ignoreOrder: true, "each new reservation is a file named for its /30 beside the old 10-*.lease files, and nothing else was created");
        AssertUntouched(directory, legacy, heldByAnOldWorker, "while the new build's workers hold their /30s");

        foreach (var worker in workers) await SayAsync(worker, "continue");
        foreach (var worker in workers) await ExitedCleanlyAsync(worker, deadline.Token);

        AssertUntouched(directory, legacy, heldByAnOldWorker, "after the new build's workers exit");
    }

    /// <summary>Where the host allocator reserves for this test's spool root, read through the production resolver rather than rebuilt here.</summary>
    private string ReservationDirectory()
    {
        using var settings = RuntimeSettings.Override(s => s with { AgentRunSpoolDirectory = _spool });

        return EgressSubnetAllocator.Host.ReservationDirectory;
    }

    /// <summary>What the allocator left behind while its pool was 10/8: one file per /30 it reserved, named for the /30 and stamped with its holder.</summary>
    private static IReadOnlyDictionary<string, byte[]> StageLegacyReservations(string directory)
    {
        Directory.CreateDirectory(directory);

        var files = new[] { LockedByAnOldWorker, "10-1-1-4_30.lease", "10-254-254-252_30.lease" }.ToDictionary(name => name, name => System.Text.Encoding.UTF8.GetBytes($"pid=4242 run={Guid.NewGuid():N} at=2026-09-01T00:00:00.0000000+00:00 ({name})\n"));

        foreach (var (name, bytes) in files) File.WriteAllBytes(Path.Combine(directory, name), bytes);

        return files;
    }

    /// <summary>Every old reservation is still there with the bytes its holder wrote — the locked one read through the handle that holds it, since any other open of it is refused.</summary>
    private static void AssertUntouched(string directory, IReadOnlyDictionary<string, byte[]> legacy, FileStream locked, string when)
    {
        foreach (var (name, bytes) in legacy)
            BytesOf(Path.Combine(directory, name), locked).ShouldBe(bytes, $"the old reservation {name} must be neither reused nor deleted {when}; check `ls -la {directory}`");
    }

    /// <summary>A file's bytes. One another process holds exclusively cannot be read at all, and among these files that can only mean a worker reserved the old /30 again, so it fails as that rather than as an I/O error.</summary>
    private static byte[] BytesOf(string path, FileStream locked)
    {
        if (path != locked.Name) return ReadUnlocked(path);

        using var copy = new MemoryStream();

        locked.Position = 0;
        locked.CopyTo(copy);

        return copy.ToArray();
    }

    private static byte[] ReadUnlocked(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch (IOException held) { throw new ShouldAssertException($"the old reservation {Path.GetFileName(path)} is locked by a worker process: it was reserved again, so an old-build worker and a new one could both hand out that /30", held); }
    }

    /// <summary>The first <paramref name="count"/> /30s of the pool, by the test's own arithmetic: 198.19.64.0/30, 198.19.64.4/30, … upward.</summary>
    private static IEnumerable<string> BottomOfThePool(int count) => Enumerable.Range(0, count).Select(i => $"198.19.{64 + i / 64}.{i % 64 * 4}/30");

    /// <summary>The name a reservation file has always had: the /30 with its dots and slash replaced. Pinned, because two builds that named one /30 differently would each lock a file the other never opens.</summary>
    private static string FileNameFor(string cidr) => cidr.Replace('/', '_').Replace('.', '-') + ".lease";

    private static IEnumerable<string> ReservationFilesIn(string directory) => Directory.GetFiles(directory, "*.lease").Select(path => Path.GetFileName(path));

    private Process StartWorker()
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };

        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "CodeSpace.IntegrationTests.runtimeconfig.json"), "--depsfile", Path.Combine(AppContext.BaseDirectory, "CodeSpace.IntegrationTests.deps.json"), typeof(EgressSubnetReservationWorker).Assembly.Location, EgressSubnetReservationWorker.Mode, _spool, PerWorker.ToString(CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);

        var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start a reserving worker process.");

        _children.Add(child);

        return child;
    }

    private async Task<string[]> ReadLeasesAsync(Process worker, CancellationToken deadline)
    {
        var leases = new string[PerWorker];

        for (var i = 0; i < PerWorker; i++) leases[i] = await ReadLineAsync(worker, deadline);

        await ExpectAsync(worker, "held", deadline);

        return leases;
    }

    private async Task ExpectAsync(Process worker, string barrier, CancellationToken deadline) =>
        (await ReadLineAsync(worker, deadline)).ShouldBe(barrier, $"a reserving worker did not reach its '{barrier}' barrier; run one by hand with `dotnet exec <IntegrationTests bin>/CodeSpace.StorageTestWorker.dll {EgressSubnetReservationWorker.Mode} {_spool} 1` and answer its barriers");

    private static async Task<string> ReadLineAsync(Process worker, CancellationToken deadline) =>
        await worker.StandardOutput.ReadLineAsync(deadline) ?? throw new Xunit.Sdk.XunitException($"a reserving worker exited before its next line: {await worker.StandardError.ReadToEndAsync(deadline)}");

    private static async Task SayAsync(Process worker, string word)
    {
        await worker.StandardInput.WriteLineAsync(word);
        await worker.StandardInput.FlushAsync();
    }

    private static async Task ExitedCleanlyAsync(Process worker, CancellationToken deadline)
    {
        await worker.WaitForExitAsync(deadline);

        worker.ExitCode.ShouldBe(0, await worker.StandardError.ReadToEndAsync(deadline));
    }

    public void Dispose()
    {
        foreach (var child in _children)
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { /* best-effort: it may have exited between the check and the kill */ }

            child.Dispose();
        }

        try { Directory.Delete(_spool, recursive: true); } catch { /* best-effort */ }
    }
}

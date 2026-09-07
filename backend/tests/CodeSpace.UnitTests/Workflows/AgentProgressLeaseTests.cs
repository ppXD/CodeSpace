using System.Text.Json;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The PROGRESS-LEASE vocabulary: the lease itself (renew / read / hold), the two signals the observer's
/// <see cref="LocalProcessRunner.ProgressWatch"/> accepts as evidence a run is working, and the decorator that makes an
/// in-flight platform request renew rather than race the no-progress watchdog. Two invariants hold all of it up, and
/// each has its own falsifier here: a signal must be evidence of WORK, never of mere EXISTENCE (a wedged run produces
/// none of them and must still die); and a renewal is honoured ONLY while the run has an execution wall deadline,
/// because without one this watchdog is the run's only bound.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentProgressLeaseTests : IDisposable
{
    private readonly List<string> _directories = new();

    private string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cs-lease-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _directories.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var directory in _directories)
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { /* best-effort */ }
    }

    // ── The lease ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_unrenewed_lease_reports_no_renewal_at_all_rather_than_a_stale_instant()
    {
        // "Nothing ever renewed this" must be distinguishable from "renewed long ago": the observer treats null as
        // "this source contributed nothing", never as a stale instant it could act on.
        new AgentProgressLease(Path.Combine(TempDirectory(), "progress")).LastRenewalUtc()
            .ShouldBeNull("a lease directory that does not exist has no renewal to report");
    }

    [Fact]
    public void Renewing_a_signal_records_an_instant_the_observer_can_read_back()
    {
        var time = new LeaseClock();
        var directory = Path.Combine(TempDirectory(), "progress");
        new AgentProgressLease(directory, time).Renew(AgentProgressSignal.PlatformRequest);

        new AgentProgressLease(directory).LastRenewalUtc().ShouldBe(time.GetUtcNow());
    }

    [Fact]
    public void A_reader_already_open_on_a_marker_keeps_the_complete_old_publication_when_another_writer_renews()
    {
        var directory = TempDirectory();
        var marker = Path.Combine(directory, "platformrequest");
        const string original = "2000-01-01T00:00:00.0000000+00:00";
        File.WriteAllText(marker, original);
        // A separate reader has opened the published inode but has not consumed it. Replacement must not truncate
        // that inode: both this reader and one opening after publication must see a complete committed timestamp.
        using var opened = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(opened);

        new AgentProgressLease(directory).Renew(AgentProgressSignal.PlatformRequest);

        reader.ReadToEnd().ShouldBe(original);
        var published = new AgentProgressLease(directory).LastRenewalUtc();
        published.ShouldNotBeNull();
        published.Value.ShouldBeGreaterThan(DateTimeOffset.Parse(original));
        Directory.GetFiles(directory).ShouldHaveSingleItem().ShouldBe(marker);
    }

    [Fact]
    public async Task A_hold_keeps_renewing_for_as_long_as_the_work_is_in_flight_and_stops_the_moment_it_answers()
    {
        var time = new LeaseClock();
        var lease = new AgentProgressLease(Path.Combine(TempDirectory(), "progress"), time);
        var answer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = lease.HoldAsync(AgentProgressSignal.PlatformRequest, () => answer.Task, CancellationToken.None);
        await time.NextTimerAsync();

        for (var heartbeat = 0; heartbeat < 3; heartbeat++)
        {
            time.Advance(AgentProgressLease.RenewalHeartbeat);
            await time.NextTimerAsync();
            lease.LastRenewalUtc().ShouldBe(time.GetUtcNow(), "the complete renewal is published before the next heartbeat is armed");
            hold.IsCompleted.ShouldBeFalse();
        }

        answer.SetResult(42);
        (await hold).ShouldBe(42);
        var released = time.GetUtcNow();
        time.Advance(AgentProgressLease.RenewalHeartbeat * 3);
        lease.LastRenewalUtc().ShouldBe(released, "released work cannot keep the watchdog alive");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_or_failure_of_the_work_stops_renewal_and_preserves_its_outcome(bool cancel)
    {
        var time = new LeaseClock();
        var lease = new AgentProgressLease(TempDirectory(), time);
        using var cancellation = new CancellationTokenSource();
        var work = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = lease.HoldAsync(AgentProgressSignal.PlatformRequest, () => work.Task.WaitAsync(cancellation.Token), cancellation.Token);
        await time.NextTimerAsync();
        var failure = new IOException("work failed");
        if (cancel) cancellation.Cancel();
        else work.SetException(failure);

        if (cancel) await Should.ThrowAsync<OperationCanceledException>(() => hold);
        else (await Should.ThrowAsync<IOException>(() => hold)).ShouldBeSameAs(failure);
        var stoppedAt = lease.LastRenewalUtc();
        time.Advance(TimeSpan.FromHours(1));
        lease.LastRenewalUtc().ShouldBe(stoppedAt);
    }

    [Fact]
    public async Task Separate_concurrent_writers_and_readers_never_observe_a_missing_or_partial_publication()
    {
        var directory = TempDirectory();
        new AgentProgressLease(directory).Renew(AgentProgressSignal.PlatformRequest);
        var writers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            var writer = new AgentProgressLease(directory);
            for (var renewal = 0; renewal < 512; renewal++) writer.Renew(AgentProgressSignal.PlatformRequest);
        })).ToArray();
        var reads = Task.Run(() =>
        {
            var reader = new AgentProgressLease(directory);
            for (var observation = 0; observation < 4096; observation++) reader.LastRenewalUtc().ShouldNotBeNull();
        });
        await Task.WhenAll(writers.Append(reads));
        Directory.GetFiles(directory).ShouldHaveSingleItem().ShouldBe(Path.Combine(directory, "platformrequest"));
    }

    [Fact]
    public void Failed_publication_cleans_its_temporary_file_and_preserves_other_committed_evidence()
    {
        var time = new LeaseClock();
        var directory = TempDirectory();
        var lease = new AgentProgressLease(directory, time);
        lease.Renew(AgentProgressSignal.SpoolOutput);
        var before = lease.LastRenewalUtc();
        Directory.CreateDirectory(Path.Combine(directory, "platformrequest"));
        time.Advance(TimeSpan.FromSeconds(1));

        Should.NotThrow(() => lease.Renew(AgentProgressSignal.PlatformRequest));

        lease.LastRenewalUtc().ShouldBe(before);
        Directory.GetFiles(directory).ShouldHaveSingleItem().ShouldBe(Path.Combine(directory, "spooloutput"));
    }

    [Fact]
    public async Task An_unwritable_lease_cannot_replace_the_work_result_or_leave_a_heartbeat_running()
    {
        var blocked = Path.Combine(TempDirectory(), "occupied");
        File.WriteAllText(blocked, "keep this file");
        var time = new LeaseClock();
        var lease = new AgentProgressLease(blocked, time);

        (await lease.HoldAsync(AgentProgressSignal.PlatformRequest, () => Task.FromResult(42), CancellationToken.None)).ShouldBe(42);
        time.Advance(TimeSpan.FromHours(1));

        File.ReadAllText(blocked).ShouldBe("keep this file");
        lease.LastRenewalUtc().ShouldBeNull();
    }

    [Fact]
    public void The_lease_the_platform_endpoint_renews_is_run_scoped_under_the_runs_own_spool_dir()
    {
        // Round-scoping the lease would silently break the approval signal on every revise round: the endpoint is opened
        // ONCE per run and outlives a round, so it would renew a directory the round's observer never reads. Pin the
        // property that can actually regress — where the directory sits — rather than comparing one helper to itself.
        var runId = Guid.NewGuid();

        LocalProcessRunner.ProgressLeaseFor(runId).LeaseDirectory.ShouldStartWith(LocalProcessRunner.SpoolDirectoryFor(runId.ToString("N")),
            customMessage: "the lease is RUN-scoped (under the run's own spool dir), so it is shared by every revise round the way the run's MCP socket is");
    }

    // ── The platform-request signal (the 600s-versus-600s collision) ─────────────────────────────────

    [Fact]
    public async Task A_blocked_platform_request_renews_the_lease_throughout_and_returns_the_inner_response_untouched()
    {
        var time = new LeaseClock();
        var lease = new AgentProgressLease(Path.Combine(TempDirectory(), "progress"), time);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new BlockingHandler(release.Task, lease, time);
        var handler = new ProgressLeaseRenewingHandler(inner, lease);
        var response = handler.HandleAsync(JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call"}""").RootElement, CancellationToken.None);
        await time.NextTimerAsync();
        for (var heartbeat = 0; heartbeat < 4; heartbeat++)
        {
            time.Advance(AgentProgressLease.RenewalHeartbeat);
            await time.NextTimerAsync();
            response.IsCompleted.ShouldBeFalse();
        }
        release.SetResult();

        var actual = await response;
        actual.ShouldNotBeNull();
        actual.Value.GetProperty("result").GetString().ShouldBe("parked-then-approved");
        inner.LeaseAgeAtWake.ShouldBeLessThan(AgentProgressLease.RenewalHeartbeat * 3);
    }

    private sealed class BlockingHandler(Task release, AgentProgressLease lease, TimeProvider time) : IMcpRequestHandler
    {
        internal TimeSpan LeaseAgeAtWake { get; private set; } = TimeSpan.MaxValue;
        public async Task<JsonElement?> HandleAsync(JsonElement request, CancellationToken cancellationToken)
        {
            await release.WaitAsync(cancellationToken);
            LeaseAgeAtWake = lease.LastRenewalUtc() is { } renewedAt ? time.GetUtcNow() - renewedAt : TimeSpan.MaxValue;
            return JsonDocument.Parse("""{"result":"parked-then-approved"}""").RootElement.Clone();
        }
    }

    // ── The watch that composes them ─────────────────────────────────────────────────────────────────

    [Fact]
    public void The_watch_declares_no_progress_when_no_signal_at_all_speaks_and_names_the_one_that_did()
    {
        // The composition: a handle with a spool and a lease, and a window short enough to expire inside the test.
        // Silence trips it, and it must STAY tripped across further passes — a wedged run is observed many times over
        // (the real loop polls every 250ms), so an assertion after a single pass would prove nothing about the second.
        var time = new LeaseClock();
        var spool = TempDirectory();
        var lease = new AgentProgressLease(Path.Combine(spool, "progress"), time);
        var window = TimeSpan.FromMilliseconds(400);
        var handle = HandleFor(spool, lease.LeaseDirectory, time.GetUtcNow().AddMinutes(30));

        var silent = new LocalProcessRunner.ProgressWatch(handle, window, time);
        time.Advance(window - TimeSpan.FromTicks(1));
        silent.NoProgress.ShouldBeFalse("the watchdog cannot expire before the configured window");
        time.Advance(TimeSpan.FromTicks(1));

        for (var pass = 0; pass < 4; pass++)
        {
            silent.Observe();

            silent.NoProgress.ShouldBeTrue($"no signal of any kind for the whole window — a genuinely wedged run must still die (pass {pass})");
            silent.RenewedBy.ShouldBeNull("nothing renewed it, so nothing may be credited");
        }

        var watched = new LocalProcessRunner.ProgressWatch(handle, window, time);
        time.Advance(window);
        lease.Renew(AgentProgressSignal.PlatformRequest);
        watched.Observe();

        watched.NoProgress.ShouldBeFalse("the same silent run is NOT stalled once a platform request is in flight — this is the parked-approval case");
        watched.RenewedBy.ShouldBe(AgentProgressSignal.PlatformRequest);
    }

    [Fact]
    public void A_lease_renewal_cannot_hold_off_the_watchdog_when_the_run_has_no_wall_deadline()
    {
        // THE BOUND. TimeoutSeconds null/≤0 is a supported operator choice and yields Deadline == MaxValue, so this
        // watchdog is the run's ONLY bound: nothing else terminates it and the reconciler cannot collect a run whose
        // observer is still heartbeating. Honouring a renewal there turns a wedged run into an immortal one holding a
        // worker, a workspace clone, a sandbox and an injected credential. So in that configuration the lease is refused
        // outright and the watch keeps exactly its pre-lease bound — spool bytes.
        var time = new LeaseClock();
        var spool = TempDirectory();
        var lease = new AgentProgressLease(Path.Combine(spool, "progress"), time);
        var window = TimeSpan.FromMilliseconds(400);
        var unbounded = HandleFor(spool, lease.LeaseDirectory, DateTimeOffset.MaxValue);

        var watch = new LocalProcessRunner.ProgressWatch(unbounded, window, time);
        time.Advance(window);
        lease.Renew(AgentProgressSignal.PlatformRequest);
        watch.Observe();

        watch.NoProgress.ShouldBeTrue("a run with no wall deadline must not be renewable — the watchdog is the only thing that can ever stop it");
        watch.RenewedBy.ShouldBeNull("the lease was not merely out-voted, it was never read");

        // ...and the ORIGINAL signal still works there, so the refusal is today's bound, not a stricter new one.
        var emitting = new LocalProcessRunner.ProgressWatch(unbounded, window, time);
        File.WriteAllText(Path.Combine(spool, "out.log"), "a line of output\n");
        emitting.Observe();

        emitting.NoProgress.ShouldBeFalse();
        emitting.RenewedBy.ShouldBe(AgentProgressSignal.SpoolOutput, "spool bytes are the pre-lease signal and are unaffected by the gate");
    }

    [Fact]
    public void The_watch_credits_spool_growth_to_the_signal_that_earned_it()
    {
        var time = new LeaseClock();
        var spool = TempDirectory();
        var window = TimeSpan.FromMilliseconds(400);
        var handle = HandleFor(spool, leaseDirectory: null, time.GetUtcNow().AddMinutes(30));

        var onSpool = new LocalProcessRunner.ProgressWatch(handle, window, time);
        File.WriteAllText(Path.Combine(spool, "out.log"), "a line of output\n");
        onSpool.Observe();

        onSpool.RenewedBy.ShouldBe(AgentProgressSignal.SpoolOutput);
        onSpool.NoProgress.ShouldBeFalse();
    }

    // Timer registration is the rendezvous after a heartbeat published. The ten-second wait only detects a
    // missing continuation; it never advances the lease age or supplies slack to the watchdog assertions.
    private sealed class LeaseClock : TimeProvider
    {
        private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        private readonly SemaphoreSlim _armed = new(0);
        public override DateTimeOffset GetUtcNow() => _time.GetUtcNow();
        public override long GetTimestamp() => _time.GetTimestamp();
        public override long TimestampFrequency => _time.TimestampFrequency;
        public void Advance(TimeSpan amount) => _time.Advance(amount);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _time.CreateTimer(callback, state, dueTime, period);
            _armed.Release();
            return timer;
        }
        public async Task NextTimerAsync() => (await _armed.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue("the renewal loop must arm its next heartbeat");
    }

    private static SandboxHandle HandleFor(string spool, string? leaseDirectory, DateTimeOffset deadline) => new()
    {
        Kind = "local",
        ProcessId = Environment.ProcessId,
        SpoolDirectory = spool,
        Deadline = deadline,
        ProgressLeaseDirectory = leaseDirectory,
    };
}

using System.Text.Json;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
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
        var lease = new AgentProgressLease(Path.Combine(TempDirectory(), "progress"));
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        lease.Renew(AgentProgressSignal.PlatformRequest);

        var renewedAt = lease.LastRenewalUtc();

        renewedAt.ShouldNotBeNull("a renewal must be readable by the observer, which lives in another object (and after a restart, another process)");
        renewedAt!.Value.ShouldBeGreaterThan(before);
    }

    [Fact]
    public async Task A_hold_keeps_renewing_for_as_long_as_the_work_is_in_flight_and_stops_the_moment_it_answers()
    {
        // The parked-approval shape: the work blocks far longer than one heartbeat, so a single renewal at entry would
        // NOT be enough — the lease has to stay fresh throughout, then go stale once the request answers.
        var lease = new AgentProgressLease(Path.Combine(TempDirectory(), "progress"));
        var hold = AgentProgressLease.RenewalHeartbeat * 3;

        var start = DateTimeOffset.UtcNow;
        var answer = await lease.HoldAsync(AgentProgressSignal.PlatformRequest, async () => { await Task.Delay(hold); return 42; }, CancellationToken.None);

        answer.ShouldBe(42, "the hold is transparent — it returns the work's own result");

        var lastRenewal = lease.LastRenewalUtc()!.Value;
        lastRenewal.ShouldBeGreaterThan(start + AgentProgressLease.RenewalHeartbeat,
            customMessage: "the hold must RE-renew on its heartbeat while blocked, not once at entry — otherwise a 10-minute approval still goes stale inside a 10-minute window");

        var released = DateTimeOffset.UtcNow;
        await Task.Delay(AgentProgressLease.RenewalHeartbeat * 3);

        lease.LastRenewalUtc()!.Value.ShouldBeLessThanOrEqualTo(released,
            customMessage: "a released hold must STOP renewing — a lease anything can renew forever is not a watchdog");
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
        // The decorator is the whole fix for the collision: a tools/call parked on a human approval blocks with zero
        // spool output, and the watchdog must see the wait as progress. Assert both halves — the lease stayed fresh
        // (measured as the observer measures it) AND the protocol response is byte-identical.
        var lease = new AgentProgressLease(Path.Combine(TempDirectory(), "progress"));
        var block = AgentProgressLease.RenewalHeartbeat * 4;

        // The window is 3 heartbeats, not 2, and the asymmetry is deliberate. The two outcomes are not equally
        // jittery: a WORKING heartbeat lands the age somewhere near one heartbeat plus whatever the thread pool adds,
        // while a DEAD one lands it at exactly the block — a value that does not move under load. So the slack belongs
        // on the passing side. At 2 heartbeats this had ~1s of room for pool jitter and went red on loaded CI; at 3 it
        // has ~2s, and a dead heartbeat still overshoots by a full second. Tightening this back trades a real
        // regression signal for nothing.
        var window = AgentProgressLease.RenewalHeartbeat * 3;
        var inner = new BlockingHandler(block) { Lease = lease };

        var handler = new ProgressLeaseRenewingHandler(inner, lease);
        var response = await handler.HandleAsync(JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call"}""").RootElement, CancellationToken.None);

        response.ShouldNotBeNull();
        response!.Value.GetProperty("result").GetString().ShouldBe("parked-then-approved", "the decorator forwards the handler's own response unchanged");

        inner.LeaseAgeAtWake.ShouldBeLessThan(window,
            customMessage: $"the lease was {inner.LeaseAgeAtWake.TotalMilliseconds:0}ms stale when the approval landed, against a {window.TotalMilliseconds:0}ms window and a {block.TotalMilliseconds:0}ms block — measured at that instant, the lease must be fresher than the no-progress window, which is exactly the comparison the observer makes before killing a run. A value at or near the block means the heartbeat never renewed at all.");
    }

    /// <summary>A handler that BLOCKS like a real tools/call parked on a human approval, and records how stale the lease was at the moment it woke — the quantity the observer's watchdog actually tests.</summary>
    private sealed class BlockingHandler : IMcpRequestHandler
    {
        private readonly TimeSpan _block;

        internal BlockingHandler(TimeSpan block) { _block = block; }

        internal TimeSpan LeaseAgeAtWake { get; private set; } = TimeSpan.MaxValue;

        internal AgentProgressLease? Lease { get; set; }

        public async Task<JsonElement?> HandleAsync(JsonElement request, CancellationToken cancellationToken)
        {
            await Task.Delay(_block, cancellationToken);

            LeaseAgeAtWake = Lease is { } lease && lease.LastRenewalUtc() is { } renewedAt ? DateTimeOffset.UtcNow - renewedAt : TimeSpan.MaxValue;

            return JsonDocument.Parse("""{"result":"parked-then-approved"}""").RootElement.Clone();
        }
    }

    // ── The watch that composes them ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_watch_declares_no_progress_when_no_signal_at_all_speaks_and_names_the_one_that_did()
    {
        // The composition: a handle with a spool and a lease, and a window short enough to expire inside the test.
        // Silence trips it, and it must STAY tripped across further passes — a wedged run is observed many times over
        // (the real loop polls every 250ms), so an assertion after a single pass would prove nothing about the second.
        var spool = TempDirectory();
        var lease = new AgentProgressLease(Path.Combine(spool, "progress"));
        var window = TimeSpan.FromMilliseconds(400);
        var handle = HandleFor(spool, lease.LeaseDirectory, DateTimeOffset.UtcNow.AddMinutes(30));

        var silent = new LocalProcessRunner.ProgressWatch(handle, window);
        await Task.Delay(window + TimeSpan.FromMilliseconds(150));

        for (var pass = 0; pass < 4; pass++)
        {
            silent.Observe();

            silent.NoProgress.ShouldBeTrue($"no signal of any kind for the whole window — a genuinely wedged run must still die (pass {pass})");
            silent.RenewedBy.ShouldBeNull("nothing renewed it, so nothing may be credited");
        }

        var watched = new LocalProcessRunner.ProgressWatch(handle, window);
        await Task.Delay(window + TimeSpan.FromMilliseconds(150));
        lease.Renew(AgentProgressSignal.PlatformRequest);
        watched.Observe();

        watched.NoProgress.ShouldBeFalse("the same silent run is NOT stalled once a platform request is in flight — this is the parked-approval case");
        watched.RenewedBy.ShouldBe(AgentProgressSignal.PlatformRequest);
    }

    [Fact]
    public async Task A_lease_renewal_cannot_hold_off_the_watchdog_when_the_run_has_no_wall_deadline()
    {
        // THE BOUND. TimeoutSeconds null/≤0 is a supported operator choice and yields Deadline == MaxValue, so this
        // watchdog is the run's ONLY bound: nothing else terminates it and the reconciler cannot collect a run whose
        // observer is still heartbeating. Honouring a renewal there turns a wedged run into an immortal one holding a
        // worker, a workspace clone, a sandbox and an injected credential. So in that configuration the lease is refused
        // outright and the watch keeps exactly its pre-lease bound — spool bytes.
        var spool = TempDirectory();
        var lease = new AgentProgressLease(Path.Combine(spool, "progress"));
        var window = TimeSpan.FromMilliseconds(400);
        var unbounded = HandleFor(spool, lease.LeaseDirectory, DateTimeOffset.MaxValue);

        var watch = new LocalProcessRunner.ProgressWatch(unbounded, window);
        await Task.Delay(window + TimeSpan.FromMilliseconds(150));
        lease.Renew(AgentProgressSignal.PlatformRequest);
        watch.Observe();

        watch.NoProgress.ShouldBeTrue("a run with no wall deadline must not be renewable — the watchdog is the only thing that can ever stop it");
        watch.RenewedBy.ShouldBeNull("the lease was not merely out-voted, it was never read");

        // ...and the ORIGINAL signal still works there, so the refusal is today's bound, not a stricter new one.
        var emitting = new LocalProcessRunner.ProgressWatch(unbounded, window);
        File.WriteAllText(Path.Combine(spool, "out.log"), "a line of output\n");
        emitting.Observe();

        emitting.NoProgress.ShouldBeFalse();
        emitting.RenewedBy.ShouldBe(AgentProgressSignal.SpoolOutput, "spool bytes are the pre-lease signal and are unaffected by the gate");
    }

    [Fact]
    public void The_watch_credits_spool_growth_to_the_signal_that_earned_it()
    {
        var spool = TempDirectory();
        var window = TimeSpan.FromMilliseconds(400);
        var handle = HandleFor(spool, leaseDirectory: null, DateTimeOffset.UtcNow.AddMinutes(30));

        var onSpool = new LocalProcessRunner.ProgressWatch(handle, window);
        File.WriteAllText(Path.Combine(spool, "out.log"), "a line of output\n");
        onSpool.Observe();

        onSpool.RenewedBy.ShouldBe(AgentProgressSignal.SpoolOutput);
        onSpool.NoProgress.ShouldBeFalse();
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

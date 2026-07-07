using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Tasks.Phases;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Drives the agent run executor end-to-end against real Postgres + the real LocalProcessRunner, with a
/// scripted test harness standing in for a CLI (so a real /bin/sh process actually runs). Proves the
/// full execution path — claim → stream events → land result — plus the exactly-once guard (an already-
/// claimed run is never re-spawned), failure, and timeout mapping.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentRunExecutorTests
{
    private readonly PostgresFixture _fixture;

    public AgentRunExecutorTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Executes_end_to_end_streaming_events_and_completing_succeeded()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        await ExecuteAsync(runId, new ScriptedHarness("printf 'step one\\nstep two\\nstep three\\n'"));

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();

        var run = await svc.GetAsync(runId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Succeeded);
        run.StartedAt.ShouldNotBeNull();
        run.CompletedAt.ShouldNotBeNull();
        run.ResultJson.ShouldNotBeNull();

        var events = await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None);
        events.Select(e => e.Text).ShouldBe(new[] { "step one", "step two", "step three" });
    }

    [Fact]
    public async Task Durable_runner_executes_via_the_spool_and_persists_a_recoverable_handle()
    {
        if (OperatingSystem.IsWindows()) return;

        // Durable is now the unconditional path (no flag) — every run launches to a spool + persists a handle.
        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        await ExecuteAsync(runId, new ScriptedHarness("printf 'durable one\\ndurable two\\n'"));

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();

        var run = await svc.GetAsync(runId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Succeeded, "the durable launch→spool→tail path completes the run");

        var events = await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None);
        events.Select(e => e.Text).ShouldBe(new[] { "durable one", "durable two" });   // lines tailed from the on-disk spool become the same normalized events

        // The recovery spine: the handle is persisted at launch (proves the jsonb runner_handle write), so a
        // restarted backend could re-find + recover this run from its spool instead of abandoning it. (Once reaped
        // after the run is terminal the handle is cleared, but the spool reaper's retention window far exceeds this
        // test's wall-clock, so it's still present here.)
        run.RunnerHandleJson.ShouldNotBeNull("the runner handle is persisted the instant the run is launched");
        var handle = JsonSerializer.Deserialize<SandboxHandle>(run.RunnerHandleJson!, AgentJson.Options)!;
        handle.Kind.ShouldBe("local");
        handle.ProcessId.ShouldBeGreaterThan(0);
        handle.SpoolDirectory.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_failed_checkpoint_flush_lands_the_run_Failed_and_does_NOT_advance_the_durable_offset()
    {
        if (OperatingSystem.IsWindows()) return;

        // D1 flush-before-offset invariant (the core durability contract). The executor's CheckpointHandleOffset
        // awaits FlushAsync (→ AppendEventsAsync) BEFORE persisting the advanced StdoutOffset. We inject a DB
        // failure on the FIRST batched flush (the first checkpoint): the offset-advancing write must NEVER run, so
        // the persisted StdoutOffset stays at its launch value (0). If the two awaits were ever reordered, a crash
        // in that gap would advance the durable offset past events that never landed → permanent silent data loss
        // on re-attach. Also pins that a DB-layer flush failure is a CLEAN Failed (generic catch), not a stranded
        // Running (only a worker tear-down / OperationCanceledException leaves it Running).
        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        // Two polls' worth of lines so a real checkpoint flush fires (>=2 lines in the first poll → one batched call).
        var instrumented = await ExecuteInstrumentedAsync(runId,
            new ScriptedHarness("printf 'l1\\nl2\\n'; sleep 0.6; printf 'l3\\nl4\\n'; sleep 0.6"),
            throwOnAppendEventsCall: 1);

        instrumented.BatchedCalls.ShouldBeGreaterThanOrEqualTo(1, "the first checkpoint flush was attempted (and faulted)");

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);

        run.Status.ShouldBe(AgentRunStatus.Failed, "a DB-layer flush failure is a clean Failed, not a stranded Running");
        (run.Error ?? "").ShouldContain(InstrumentedAgentRunService.AppendEventsFaultMessage, customMessage: "the run carries the redacted flush-fault cause");

        JsonSerializer.Deserialize<SandboxHandle>(run.RunnerHandleJson!, AgentJson.Options)!.StdoutOffset
            .ShouldBe(0, "the throw in FlushAsync short-circuited BEFORE SetRunnerHandleAsync — the durable offset never advanced past unflushed events");
    }

    [Fact]
    public async Task Durable_path_batches_event_writes_one_flush_per_checkpoint_never_one_per_line()
    {
        if (OperatingSystem.IsWindows()) return;

        // D1 perf thesis (otherwise behaviourally uncovered): "one batched INSERT per checkpoint, not one per
        // line". A regression that flushed per-line (or bypassed the buffer to AppendEventAsync) passes every
        // ORDER/scale test while silently destroying the round-trip reduction. We count: K lines emitted in P
        // bursts must issue ~P batched calls (a >4x reduction), and the single-row path must stay at ZERO.
        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        // 120 lines in 12 bursts of 10 (each burst lands in one poll; sleep 0.4 > the 250ms poll → one checkpoint per burst).
        const int total = 120;
        var instrumented = await ExecuteInstrumentedAsync(runId,
            new ScriptedHarness("for b in $(seq 0 11); do for i in $(seq 0 9); do printf 'line-%03d\\n' $((b*10+i)); done; sleep 0.4; done"));

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();

        (await svc.GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded);

        var events = await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None);
        events.Count.ShouldBe(total, "every emitted line persisted");
        instrumented.TotalEvents.ShouldBe(total, "the counter saw every event through the batched path");
        instrumented.PerEventCalls.ShouldBe(0, "the PRIMARY per-line guard: the durable streaming hot path NEVER falls back to the single-row append (a revert to AppendEventAsync per line trips here)");
        instrumented.BatchedCalls.ShouldBeGreaterThan(1, "real per-checkpoint flushing — not one giant end-of-run flush");
        instrumented.BatchedCalls.ShouldBeLessThanOrEqualTo(total / 4, customMessage: $"the round-trip-reduction guard: even a per-line flush THROUGH the batched path (batch-of-1) is caught — a ≥4x reduction means ≤{total / 4} flushes for {total} events; saw {instrumented.BatchedCalls}");
    }

    [Fact]
    public async Task A_256_cap_auto_flush_commits_durably_with_the_offset_left_unadvanced()
    {
        if (OperatingSystem.IsWindows()) return;

        // D1 cap-flush durability. BufferAsync's MaxBuffered=256 auto-flush is the ONE flush trigger NOT paired
        // with an offset persist (it fires mid-onLine-sweep, before the poll's onCheckpoint). We emit >256 lines
        // in one burst so the cap trips (call #1 commits 256), then FAULT the following checkpoint flush (call #2):
        // the run fails, but the cap-flushed 256 events stay DURABLE and the StdoutOffset stays at 0 — proving a
        // cap flush commits events with no paired offset advance. A regression that advanced the offset on a cap
        // flush would lose this burst on reattach; one that drained the buffer before the append would lose them
        // outright. (Bounded re-delivery of such offset-behind-committed events on reattach is pinned by the
        // reattach crash-window test; here we pin the cap flush's durability + offset-unpaired property.)
        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        // Lead with a sleep so the first spool poll(s) read an EMPTY file (the child is still sleeping → no
        // checkpoint), then the whole 400-line burst is written in one shot (≈10ms) and lands in a single LATER
        // poll's read — deterministically tripping the 256 cap on flush call #1, regardless of CI scheduling
        // jitter (the burst can't be bisected by a poll because it's written entirely within one inter-poll gap).
        var instrumented = await ExecuteInstrumentedAsync(runId,
            new ScriptedHarness("sleep 0.4; for i in $(seq 1 400); do printf 'line-%04d\\n' $i; done; sleep 0.8"),
            throwOnAppendEventsCall: 2);

        instrumented.BatchedCalls.ShouldBeGreaterThanOrEqualTo(2, "the cap auto-flush (call 1) then the faulting checkpoint flush (call 2) both fired");

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();

        var run = await svc.GetAsync(runId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Failed, "the checkpoint flush after the cap flush faulted");

        var events = await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None);
        events.Count.ShouldBe(256, "the cap auto-flush committed exactly its 256-event batch — durable despite the later flush failing");
        events.Select(e => e.Text).ShouldBe(Enumerable.Range(1, 256).Select(i => $"line-{i:D4}"), "the cap-flushed prefix is in order");

        JsonSerializer.Deserialize<SandboxHandle>(run.RunnerHandleJson!, AgentJson.Options)!.StdoutOffset
            .ShouldBe(0, "the cap flush did NOT advance the durable offset — only a checkpoint does, and that one faulted before SetRunnerHandleAsync");
    }

    [Fact]
    public async Task Durable_path_persists_a_600_line_stream_crossing_the_256_cap_in_strict_global_order()
    {
        if (OperatingSystem.IsWindows()) return;

        // D1 ordering through the REAL BufferedEventWriter at scale: a 600-line burst trips BufferAsync's
        // MaxBuffered=256 auto-flush (the one flush trigger NOT aligned to a checkpoint), then the per-poll
        // checkpoint flush + the final flush concatenate into ONE globally-monotonic per-run sequence. A
        // double-flush of a buffered batch, or a 256-flush/checkpoint-flush collision, would scramble or
        // duplicate the cursor invisibly. Existing ordering tests call the service directly with pre-built
        // batches; the only writer-driven tests emit <=6 lines and never cross the cap.
        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        const int total = 600;
        await ExecuteAsync(runId, new ScriptedHarness("for i in $(seq 1 600); do printf 'line-%04d\\n' $i; done; sleep 0.3"));

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();

        (await svc.GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded);

        var events = await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None);
        events.Count.ShouldBe(total, "no line lost crossing the 256 cap + multiple checkpoints + the final flush");
        events.Select(e => e.Text).ShouldBe(Enumerable.Range(1, total).Select(i => $"line-{i:D4}"), "strict global order preserved across every flush boundary");
        events.Select(e => e.Sequence).SequenceEqual(events.Select(e => e.Sequence).OrderBy(s => s)).ShouldBeTrue("per-run sequence strictly ascending, no gaps, no duplicates");
    }

    [Fact]
    public async Task Injects_the_decrypted_model_credential_into_the_child_env_and_freezes_only_the_reference()
    {
        if (OperatingSystem.IsWindows()) return;

        const string plaintextKey = "sk-injected-keystone-value";

        var teamId = await SeedTeamAsync();
        var credId = await SeedModelCredentialAsync(teamId, "scripted-provider", plaintextKey);
        var runId = await CreateRunWithCredentialAsync(teamId, credId);

        // The projecting harness maps the resolved credential to SCRIPTED_MODEL_KEY; the script reports only the
        // key's PRESENCE (never its value), so the run log proves injection without the test itself leaking it.
        await ExecuteAsync(runId, new ProjectingScriptedHarness("scripted-provider", "SCRIPTED_MODEL_KEY",
            "if [ -n \"$SCRIPTED_MODEL_KEY\" ]; then echo KEY_PRESENT; else echo KEY_ABSENT; fi"));

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();
        var run = await svc.GetAsync(runId, CancellationToken.None);

        run.Status.ShouldBe(AgentRunStatus.Succeeded);
        (await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None)).Select(e => e.Text)
            .ShouldContain("KEY_PRESENT", "the decrypted credential was injected into the REAL child process env");

        // Staging froze only the Guid reference — the secret never touches the persisted run.
        run.TaskJson.ShouldContain(credId.ToString());
        run.TaskJson.ShouldNotContain(plaintextKey);
        run.ResultJson.ShouldNotBeNull();
        run.ResultJson!.ShouldNotContain(plaintextKey);
    }

    [Fact]
    public async Task A_pinned_credential_from_another_team_lands_the_run_failed_clean()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamA = await SeedTeamAsync();
        var teamB = await SeedTeamAsync();
        var credInB = await SeedModelCredentialAsync(teamB, "scripted-provider", "sk-team-b-secret");

        // Run belongs to team A but pins team B's credential → the resolver must refuse and the run fail clean.
        var runId = await CreateRunWithCredentialAsync(teamA, credInB);

        await ExecuteAsync(runId, new ProjectingScriptedHarness("scripted-provider", "SCRIPTED_MODEL_KEY", "echo should-not-run"));

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);

        run.Status.ShouldBe(AgentRunStatus.Failed, "a pinned credential from another team is unresolvable — fail clean, never use it");
        run.Error.ShouldNotBeNull();
        run.Error!.ShouldNotContain("sk-team-b-secret", customMessage: "the failure must never echo a key (and none was decrypted anyway)");
    }

    [Fact]
    public async Task Redacts_an_echoed_model_key_from_the_append_only_log_and_result()
    {
        if (OperatingSystem.IsWindows()) return;

        const string key = "sk-echo-leak-value-9f3a7c";

        var teamId = await SeedTeamAsync();
        var credId = await SeedModelCredentialAsync(teamId, "scripted-provider", key);
        var runId = await CreateRunWithCredentialAsync(teamId, credId);

        // The harness ECHOES its injected key to stdout — the real leak vector (a CLI printing its key in a
        // banner / 401 body). The redactor must mask it before the append-only event log freezes it.
        await ExecuteAsync(runId, new KeyEchoingHarness("scripted-provider", "SCRIPTED_MODEL_KEY"));

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();
        var run = await svc.GetAsync(runId, CancellationToken.None);
        var events = await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None);

        events.ShouldNotBeEmpty();
        foreach (var e in events)
        {
            e.Text.ShouldNotContain(key, customMessage: "the live-log text must never carry the key");
            (e.DataJson ?? "").ShouldNotContain(key, customMessage: "the structured payload must never carry the key");
        }
        events.ShouldContain(e => e.Text.Contains(SecretRedactor.Placeholder), "the echoed key is masked, not silently dropped");

        run.ResultJson!.ShouldNotContain(key, customMessage: "the result (summary folded from the events) must not carry the key");
        (run.Error ?? "").ShouldNotContain(key);
    }

    [Fact]
    public async Task Does_not_re_run_an_already_claimed_run()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);   // another worker already claimed it

        await ExecuteAsync(runId, new ScriptedHarness("printf 'must not run\\n'"));

        using var verify = _fixture.BeginScope();
        var svc = verify.Resolve<IAgentRunService>();
        (await svc.GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Running);   // untouched
        (await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None)).ShouldBeEmpty();                  // the harness was never spawned
    }

    [Theory]
    [InlineData(WorkflowRunStatus.Cancelled)]
    [InlineData(WorkflowRunStatus.Failure)]
    [InlineData(WorkflowRunStatus.Success)]
    public async Task Cancels_a_branch_run_whose_parent_workflow_is_terminal_at_the_claim_point_without_launching(WorkflowRunStatus parentStatus)
    {
        if (OperatingSystem.IsWindows()) return;

        // The post-claim TOCTOU guard: the reconciler's still-Queued check can't see a parent that flips terminal in
        // the window before the executor claims. After winning the Queued→Running claim, the executor re-reads the
        // parent; a terminal parent cancels this run (never spawning a sandbox under a dead workflow) and resumes the
        // parent off the Cancelled state. The harness echoes "must-not-run" — its absence from the log proves no launch.
        var teamId = await SeedTeamAsync();
        var parentRunId = await SeedParentWorkflowRunAsync(teamId, parentStatus);
        var runId = await CreateBranchRunAsync(teamId, parentRunId);

        await ExecuteAsync(runId, new ScriptedHarness("printf 'must-not-run\\n'"));

        using var verify = _fixture.BeginScope();
        var svc = verify.Resolve<IAgentRunService>();
        var run = await svc.GetAsync(runId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Cancelled, "a branch run under a terminal parent is cancelled at the claim, not launched");
        run.Error.ShouldBe(AgentRunExecutor.ParentTerminalAtClaimError);
        (await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None)).ShouldBeEmpty("no sandbox was spawned for an already-dead workflow");
    }

    [Fact]
    public async Task Runs_a_branch_run_normally_when_its_parent_workflow_is_still_live()
    {
        if (OperatingSystem.IsWindows()) return;

        // The non-breaking half of the claim-point guard: a LIVE parent (Suspended/Pending/Running) must proceed
        // EXACTLY as today — claim, run the harness, complete Succeeded. Only a TERMINAL parent aborts.
        var teamId = await SeedTeamAsync();
        var parentRunId = await SeedParentWorkflowRunAsync(teamId, WorkflowRunStatus.Suspended);
        var runId = await CreateBranchRunAsync(teamId, parentRunId);

        await ExecuteAsync(runId, new ScriptedHarness("printf 'ran\\n'"));

        using var verify = _fixture.BeginScope();
        var svc = verify.Resolve<IAgentRunService>();
        (await svc.GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded, "a live parent leaves the run to execute unchanged");
        (await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None)).Select(e => e.Text).ShouldContain("ran");
    }

    private async Task<Guid> SeedParentWorkflowRunAsync(Guid teamId, WorkflowRunStatus status)
    {
        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(SystemUsers.SeederId, teamId, Roles.Admin))
            workflowId = await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
            {
                Name = "agent-parent-" + Guid.NewGuid().ToString("N")[..6],
                Description = null,
                Definition = WorkflowsTestSeed.MinimalDefinition(),
                Activations = new List<WorkflowActivationInput>(),
                Enabled = true,
            });

        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        // Flip the parent to the target status via a pure UPDATE (the audit interceptor refuses a status change on a
        // tracked entity) — the same way the map-resume tests stage a terminal/live parent.
        using var flip = _fixture.BeginScope();
        await flip.Resolve<CodeSpaceDbContext>().WorkflowRun
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, status));

        return runId;
    }

    private async Task<Guid> CreateBranchRunAsync(Guid teamId, Guid parentRunId)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "branch", Harness = "scripted", Model = "test-model" },
            teamId, parentRunId, "map#0", iterationKey: "", cancellationToken: CancellationToken.None);
        return run.Id;
    }

    [Fact]
    public async Task Nonzero_harness_exit_completes_failed()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        await ExecuteAsync(runId, new ScriptedHarness("printf 'working\\n'; exit 7"));

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Failed);
        // The harness's failure reason must reach the persisted run record — not just the status — so the
        // operator sees WHY it failed. (The real harnesses fold the CLI's final message into this; the
        // stub carries the exit code, which is what we assert reaches AgentRun.error here.)
        (run.Error ?? "").ShouldContain("7", customMessage: "the run's failure reason must be persisted to AgentRun.error, not swallowed");
    }

    [Fact]
    public async Task Sandbox_timeout_completes_timed_out()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId, timeoutSeconds: 1);

        await ExecuteAsync(runId, new ScriptedHarness("sleep 10"));

        using var verify = _fixture.BeginScope();
        (await verify.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.TimedOut);
    }

    [Fact]
    public void Executor_is_registered_in_the_container()
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<IAgentRunExecutor>().ShouldNotBeNull();
    }

    [Fact]
    public async Task Clones_the_bound_repository_into_the_workspace_and_runs_there()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var origin = new TempDir();
        await SeedLocalRepoAsync(origin.Path, "README.md", "hello-from-repo");
        var repoId = await SeedRepositoryAsync(teamId, new Uri(origin.Path).AbsoluteUri, defaultBranch: "main");
        var runId = await CreateRepoRunAsync(teamId, repoId);

        // The scripted harness `cat README.md` only sees the file if the executor cloned the repo AND
        // ran the harness with the clone as its working directory.
        await ExecuteAsync(runId, new ScriptedHarness("cat README.md"));

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();

        (await svc.GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded);
        (await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None)).Select(e => e.Text)
            .ShouldContain("hello-from-repo", "the harness ran inside the cloned workspace and read the repo file");
    }

    [Fact]
    public async Task Workspace_clone_failure_lands_the_run_failed_not_stuck()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        // A repo whose clone URL points at a non-existent local path → the clone fails. The executor must
        // land the run Failed (its catch maps the WorkspaceException), never leave it stuck Running.
        var missing = new Uri(Path.Combine(Path.GetTempPath(), "cs-missing-" + Guid.NewGuid().ToString("N"))).AbsoluteUri;
        var repoId = await SeedRepositoryAsync(teamId, missing, "main");
        var runId = await CreateRepoRunAsync(teamId, repoId);

        await ExecuteAsync(runId, new ScriptedHarness("echo should-not-run"));

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Failed, "a workspace clone failure lands the run Failed, not stuck Running");
        run.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task Captures_the_git_diff_ground_truth_of_the_agents_edits_into_the_result()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var origin = new TempDir();
        await SeedLocalRepoAsync(origin.Path, "README.md", "hello\n");
        var repoId = await SeedRepositoryAsync(teamId, new Uri(origin.Path).AbsoluteUri, defaultBranch: "main");
        var runId = await CreateRepoRunAsync(teamId, repoId);

        // The scripted harness makes a REAL edit in the clone; the executor must capture the git diff
        // ground truth into the result (overriding any event-parsed list) before the workspace is disposed.
        await ExecuteAsync(runId, new ScriptedHarness("printf 'added by the agent\\n' >> README.md; echo edited"));

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Succeeded);

        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
        result.ChangedFiles.ShouldContain("README.md", customMessage: "git ground truth — the captured diff lists the file the agent actually edited");
        result.Patch.ShouldContain("added by the agent", customMessage: "the unified diff carries the actual change, captured from git before the clone was removed");
    }

    [Fact]
    public async Task A_multi_repo_run_surfaces_per_repo_results_branches_and_a_change_set_id()
    {
        // Multi-repo PR3 end-to-end through real execution + persistence: a workspace with two WRITABLE repos
        // (web primary, api) + one READ-ONLY context repo (docs) yields a RepositoryResults entry per WRITABLE repo
        // — each carrying its repo identity AND its pushed branch — plus a run-id-derived ChangeSetId, all round-tripped
        // through result_jsonb. The read-only repo is excluded; the top-level fields keep mirroring the PRIMARY repo.
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var webId = Guid.NewGuid();
        var apiId = Guid.NewGuid();
        var docsId = Guid.NewGuid();
        var runId = await CreateMultiRepoRunAsync(teamId, push: true,
            ("web", webId, WorkspaceAccess.Write, true),
            ("api", apiId, WorkspaceAccess.Write, false),
            ("docs", docsId, WorkspaceAccess.Read, false));

        var provider = new MultiRepoRecordingWorkspaceProvider(repos: new[]
        {
            ("web", WorkspaceAccess.Write, true),
            ("api", WorkspaceAccess.Write, false),
            ("docs", WorkspaceAccess.Read, false),
        });

        await ExecuteWithRecordingWorkspaceAsync(runId, new ScriptedHarness("printf 'done\\n'"), provider, CancellationToken.None);

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Succeeded);

        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
        var branch = AgentRunExecutor.BuildBranchName(runId);

        result.ChangeSetId.ShouldBe(AgentRunExecutor.ChangeSetIdFor(runId), "a multi-repo run stamps a run-id-derived change-set id");
        result.RepositoryResults.Select(r => r.Alias).ShouldBe(new[] { "web", "api" }, ignoreOrder: true,
            "only WRITABLE repos form the change set — the read-only 'docs' context repo is excluded");

        var web = result.RepositoryResults.Single(r => r.Alias == "web");
        var api = result.RepositoryResults.Single(r => r.Alias == "api");
        web.RepositoryId.ShouldBe(webId, "each per-repo result carries its repo identity (the downstream PR-open key)");
        api.RepositoryId.ShouldBe(apiId);
        web.ChangedFiles.ShouldBe(new[] { "web.txt" });
        web.ProducedBranch.ShouldBe(branch, "each writable repo's pushed branch round-trips through result_jsonb");
        api.ProducedBranch.ShouldBe(branch);
        web.BaseBranch.ShouldBe("main-web", "each per-repo result carries its resolved base branch — the PR target a downstream git.open_change_set binds verbatim");
        api.BaseBranch.ShouldBe("main-api");
        // S7-C0: each writable repo's per-repo DIFF is captured + round-trips through result_jsonb — the durable,
        // base-anchored input the supervisor's per-repo on-disk integration consumes (small here → stays inline).
        web.Patch.ShouldBe("diff for web", "each writable repo's per-repo diff round-trips through result_jsonb");
        api.Patch.ShouldBe("diff for api");

        result.ChangedFiles.ShouldBe(new[] { "web.txt" }, "the top-level fields mirror the PRIMARY repo");
        result.ProducedBranch.ShouldBe(branch, "the top-level branch mirrors the primary repo's branch");
        result.Patch.ShouldBe("diff for web", "the top-level patch mirrors the PRIMARY repo's per-repo diff");
    }

    [Fact]
    public async Task A_secondary_repo_capture_failure_drops_only_that_repo_and_keeps_the_change_set()
    {
        // The per-repo capture-isolation fix: a SECONDARY repo's capture hiccup drops only that repo — it must never
        // abort the whole change set (which would silently degrade a multi-repo run to look single-repo) nor fail the run.
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateMultiRepoRunAsync(teamId, push: false,
            ("web", Guid.NewGuid(), WorkspaceAccess.Write, true),
            ("api", Guid.NewGuid(), WorkspaceAccess.Write, false));

        var provider = new MultiRepoRecordingWorkspaceProvider(
            repos: new[] { ("web", WorkspaceAccess.Write, true), ("api", WorkspaceAccess.Write, false) },
            throwCaptureFor: new HashSet<string> { "api" });

        await ExecuteWithRecordingWorkspaceAsync(runId, new ScriptedHarness("printf 'done\\n'"), provider, CancellationToken.None);

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Succeeded, "a secondary repo's capture hiccup never fails the run");

        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
        result.RepositoryResults.Select(r => r.Alias).ShouldBe(new[] { "web" }, "the failed 'api' repo is dropped; the captured 'web' repo is kept");
        result.ChangeSetId.ShouldBe(AgentRunExecutor.ChangeSetIdFor(runId), "the change set is still stamped — the run is NOT degraded to look single-repo");
    }

    [Fact]
    public async Task A_single_repo_run_leaves_repository_results_empty_and_no_change_set_id()
    {
        // The byte-identical acceptance gate: a single-repo workspace (Repositories.Count == 1) takes the unchanged
        // path — RepositoryResults stays empty and ChangeSetId null; only the top-level fields carry the outcome.
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        await ExecuteWithRecordingWorkspaceAsync(runId, new ScriptedHarness("printf 'done\\n'"), new RecordingWorkspaceProvider(), CancellationToken.None);

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);

        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
        result.RepositoryResults.ShouldBeEmpty("a single-repo run surfaces no per-repo change set");
        result.ChangeSetId.ShouldBeNull();
    }

    [Fact]
    public async Task A_capture_infra_failure_does_not_flip_a_succeeded_run_to_failed()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var origin = new TempDir();
        await SeedLocalRepoAsync(origin.Path, "README.md", "hello\n");
        var repoId = await SeedRepositoryAsync(teamId, new Uri(origin.Path).AbsoluteUri, defaultBranch: "main");
        var runId = await CreateRepoRunAsync(teamId, repoId);

        // The harness SUCCEEDS (exit 0) but removes its own clone directory, so the post-run git-diff
        // capture spawns git with a now-missing working directory — a RAW infra failure (not a non-zero
        // git exit). Best-effort capture must swallow it and leave the run Succeeded, never flip it Failed.
        await ExecuteAsync(runId, new ScriptedHarness("d=\"$(pwd)\"; cd /tmp 2>/dev/null; rm -rf \"$d\"; echo done"));

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Succeeded, "a capture-time infrastructure failure is best-effort — the successful run must not become Failed over it");
    }

    [Fact]
    public async Task A_run_with_no_workspace_has_an_empty_patch()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);   // no RepositoryId → no workspace → nothing to capture

        await ExecuteAsync(runId, new ScriptedHarness("printf 'analysis only\\n'"));

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);

        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
        result.Patch.ShouldBeEmpty("no workspace → the capture step is a no-op and the patch stays empty");
    }

    [Fact]
    public async Task Persists_token_usage_extracted_from_the_run_stream()
    {
        if (OperatingSystem.IsWindows()) return;

        // D3b-i end-to-end: a harness whose BuildResult reads usage off the event stream (like the real Codex/
        // Claude adapters) must land that figure in the persisted result_jsonb — the cost-accounting input the
        // per-team budget cap consumes. Drives the REAL executor + spool path, then reads it back from the DB.
        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        const string script = """printf 'working on it\n{"type":"token_count","info":{"total_token_usage":{"input_tokens":1450,"output_tokens":260}}}\n'""";
        await ExecuteAsync(runId, new UsageReportingHarness(script));

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Succeeded);

        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
        result.TokenUsage.ShouldNotBeNull("the usage reported in the stream is persisted to result_jsonb for cost accounting");
        result.TokenUsage!.InputTokens.ShouldBe(1450);
        result.TokenUsage.OutputTokens.ShouldBe(260);
    }

    [Fact]
    public async Task Projects_the_captured_token_usage_onto_the_agent_metric_end_to_end()
    {
        if (OperatingSystem.IsWindows()) return;

        // The capture→persist→PROJECT chain joined deterministically — the gap the sibling token-persist test leaves
        // open. A harness emits a KNOWN token_count, the REAL executor persists it to result_jsonb, and the SAME
        // AgentMetricsReader the run-detail outline/terminal consume projects that figure onto the per-agent metric.
        // Proves the tokens don't just persist but actually REACH the projected ref end-to-end, without a real model.
        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        const string script = """printf 'working on it\n{"type":"token_count","info":{"total_token_usage":{"input_tokens":1450,"output_tokens":260}}}\n'""";
        await ExecuteAsync(runId, new UsageReportingHarness(script));

        using var scope = _fixture.BeginScope();
        var metrics = await scope.Resolve<AgentMetricsReader>().ReadAsync(teamId, new[] { runId }, DateTimeOffset.UtcNow, CancellationToken.None);

        var m = metrics[runId];
        m.Status.ShouldBe(AgentRunStatus.Succeeded);
        m.InputTokens.ShouldBe(1450, "the harness-emitted token_count is captured, persisted, AND projected onto the metric the UI reads");
        m.OutputTokens.ShouldBe(260);
        m.DurationMs.ShouldNotBeNull("a completed run carries a real duration off its persisted timestamps");
    }

    [Fact]
    public async Task Captures_the_faithful_transcript_including_a_line_ParseEvent_dropped()
    {
        if (OperatingSystem.IsWindows()) return;

        // D3a faithfulness: the transcript is the RAW redacted stream, captured BEFORE the parse filter — not a
        // re-render of the parsed events. The scripted harness's ParseEvent DROPS whitespace-only lines (returns
        // null), so the middle line never becomes an event; it MUST still be in the transcript. This is the whole
        // point of a separate transcript: "replay the exact session", including what the normalizer discarded.
        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        await ExecuteAsync(runId, new ScriptedHarness("printf 'alpha\\n   \\nbeta\\n'"));

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();

        var run = await svc.GetAsync(runId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Succeeded);

        // The parsed event log dropped the whitespace-only line — only the two real lines survive normalization.
        (await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None)).Select(e => e.Text).ShouldBe(new[] { "alpha", "beta" });

        // The transcript is faithful: a small one stays inline in result_jsonb and carries EVERY raw line IN ORDER —
        // including the whitespace-only middle line ParseEvent dropped, sandwiched between alpha and beta.
        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
        result.TranscriptArtifactId.ShouldBeNull("a tiny transcript stays inline");
        var lines = result.Transcript.Split('\n');
        lines[..3].ShouldBe(new[] { "alpha", "   ", "beta" }, "every raw line is in stream order — the dropped whitespace line is preserved between the two real ones, captured before the parse filter");
    }

    [Fact]
    public async Task A_large_run_transcript_is_offloaded_to_an_artifact_and_recoverable_in_full()
    {
        if (OperatingSystem.IsWindows()) return;

        // D3a high-perf: a real run's transcript can be large (a long agent session). It must NOT bloat the
        // result_jsonb row read on every resume — it's offloaded to the content-addressed store, the result keeps
        // only the ref, and the full raw stream round-trips on demand. Drives the REAL executor + spool path.
        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        // ~18 KiB (500 × ~37 B) over the 8 KiB inline threshold; distinctive head/tail lines to prove full-fidelity recovery.
        await ExecuteAsync(runId, new ScriptedHarness("for i in $(seq 1 500); do printf 'transcript line %04d padding-padding\\n' $i; done"));

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();

        var run = await svc.GetAsync(runId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Succeeded);

        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
        result.Transcript.ShouldBe("", "the large transcript was moved out of result_jsonb");
        result.TranscriptArtifactId.ShouldNotBeNull("the result keeps only a reference to the offloaded transcript");

        var artifact = await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, result.TranscriptArtifactId!.Value, CancellationToken.None);
        artifact.ShouldNotBeNull();
        var recovered = System.Text.Encoding.UTF8.GetString(artifact!.Bytes);
        recovered.ShouldContain("transcript line 0001 padding-padding", customMessage: "the first raw line round-trips from the store");
        recovered.ShouldContain("transcript line 0500 padding-padding", customMessage: "the last raw line round-trips — the whole session is durable, not a head-truncated sample");
        artifact.ContentType.ShouldBe("text/plain");
    }

    private async Task<Guid> CreateRepoRunAsync(Guid teamId, Guid repositoryId)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "edit", Harness = "scripted", Model = "test-model", RepositoryId = repositoryId },
            teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);
        return run.Id;
    }

    private async Task<Guid> CreateRunWithCredentialAsync(Guid teamId, Guid modelCredentialId)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "scripted", Harness = "scripted-projector", Model = "test-model", ModelCredentialId = modelCredentialId },
            teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);
        return run.Id;
    }

    private async Task<Guid> SeedModelCredentialAsync(Guid teamId, string provider, string plaintextKey)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var id = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = id,
            TeamId = teamId,
            Provider = provider,
            DisplayName = "test cred",
            EncryptedApiKey = scope.Resolve<CodeSpace.Core.Services.Credentials.IPayloadEncryptor>().Encrypt(plaintextKey),
            Status = CredentialStatus.Active,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedRepositoryAsync(Guid teamId, string cloneUrlHttps, string defaultBranch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.Git, DisplayName = "local", BaseUrl = "https://local" });

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = null,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = defaultBranch, CloneUrlHttps = cloneUrlHttps, WebUrl = "https://local/org/repo",
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private static async Task<bool> GitAvailableAsync()
    {
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    private static async Task SeedLocalRepoAsync(string dir, string file, string content)
    {
        await RunGitInAsync(dir, "init", "-b", "main");
        await RunGitInAsync(dir, "config", "user.email", "test@codespace.dev");
        await RunGitInAsync(dir, "config", "user.name", "Test");
        await RunGitInAsync(dir, "config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(dir, file), content);
        await RunGitInAsync(dir, "add", ".");
        await RunGitInAsync(dir, "commit", "-m", "seed");
    }

    private static async Task RunGitInAsync(string workdir, params string[] args)
    {
        var result = await new LocalProcessRunner().RunAsync(
            new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);
        if (result.Status != SandboxStatus.Success) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs-agent-origin-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ } }
    }

    [Fact]
    public async Task Completes_succeeded_even_after_a_heartbeat_ping_fires_midrun()
    {
        if (OperatingSystem.IsWindows()) return;

        // Regression: a run that outlives one heartbeat interval used to get stranded Running. The heartbeat
        // loop pings on a SEPARATE DbContext, bumping the row's xmin; a tracked CompleteAsync then failed its
        // optimistic-concurrency check and never landed terminal → the reconciler later abandoned it. Status-
        // guarded CAS completion fixes it. Window 9s → interval 5s → a ping lands during the 8s run.
        var original = Environment.GetEnvironmentVariable(AgentRunLiveness.WindowEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AgentRunLiveness.WindowEnvVar, "00:00:09");

            var teamId = await SeedTeamAsync();
            var runId = await CreateScriptedRunAsync(teamId);

            await ExecuteAsync(runId, new ScriptedHarness("sleep 8; printf 'done\\n'"));

            using var scope = _fixture.BeginScope();
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.Succeeded, $"a heartbeat bumping xmin mid-run must NOT block completion (actual={run.Status}, err={run.Error})");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentRunLiveness.WindowEnvVar, original);
        }
    }

    [Fact]
    public async Task Heartbeat_loop_advances_heartbeat_during_a_long_quiet_run()
    {
        if (OperatingSystem.IsWindows()) return;

        // Force the heartbeat cadence to its 5s floor (window/3 of 9s → floored 5s) so a ping lands during
        // the ~8s run. The harness sleeps emitting NO events, so ONLY the heartbeat loop — pinging through
        // its dedicated-scope DbContext concurrently with the (empty) event stream — can advance HeartbeatAt.
        var original = Environment.GetEnvironmentVariable(AgentRunLiveness.WindowEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AgentRunLiveness.WindowEnvVar, "00:00:09");

            var teamId = await SeedTeamAsync();
            var runId = await CreateScriptedRunAsync(teamId);

            await ExecuteAsync(runId, new ScriptedHarness("sleep 8"));

            using var scope = _fixture.BeginScope();
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);

            run.StartedAt.ShouldNotBeNull();
            run.HeartbeatAt.ShouldNotBeNull();
            // MarkRunning stamps StartedAt ≈ HeartbeatAt at claim; a ≥3s advance can only come from a mid-run
            // ping (the 5s-floor interval fired before the 8s run ended). Regressing to the shared _runs
            // context — or dropping the loop — would leave HeartbeatAt at its claim value and fail this.
            (run.HeartbeatAt!.Value - run.StartedAt!.Value).ShouldBeGreaterThan(TimeSpan.FromSeconds(3));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentRunLiveness.WindowEnvVar, original);
        }
    }

    [Fact]
    public async Task Worker_teardown_mid_run_leaves_the_workspace_for_reattach()
    {
        if (OperatingSystem.IsWindows()) return;

        // A graceful worker tear-down (cancellation) while the detached agent is still running must NOT delete the
        // workspace clone out from under it — the agent's cwd lives inside it. The re-attach reuses the surviving
        // clone; the janitor reaps it by age if no re-attach ever claims it.
        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        var provider = new RecordingWorkspaceProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            ExecuteWithRecordingWorkspaceAsync(runId, new ScriptedHarness("sleep 3"), provider, cts.Token));

        provider.PreparedDirectory.ShouldNotBeNull();
        provider.Disposed.ShouldBeFalse("a worker tear-down must leave the workspace clone for the re-attach, not delete it under the live agent");
        Directory.Exists(provider.PreparedDirectory!).ShouldBeTrue("the workspace clone must still be on disk after the tear-down");

        // PR leaves the clone for the janitor in prod; the test removes its own temp dir.
        try { Directory.Delete(provider.PreparedDirectory!, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Successful_run_disposes_the_workspace()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        var provider = new RecordingWorkspaceProvider();

        await ExecuteWithRecordingWorkspaceAsync(runId, new ScriptedHarness("printf 'done\\n'"), provider, CancellationToken.None);

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded);
        provider.Disposed.ShouldBeTrue("a terminal run owns the workspace cleanup");
        Directory.Exists(provider.PreparedDirectory!).ShouldBeFalse("the clone is removed once the run lands terminal");
    }

    private async Task ExecuteWithRecordingWorkspaceAsync(Guid runId, IAgentHarness harness, IWorkspaceProvider provider, CancellationToken cancellationToken)
    {
        using var scope = _fixture.BeginScope();
        var executor = new AgentRunExecutor(
            scope.Resolve<IAgentRunService>(),
            new AgentHarnessRegistry(new[] { harness }),
            new HarnessModelReconciler(new AgentHarnessRegistry(new[] { harness }), scope.Resolve<IModelPoolSelector>(), scope.Resolve<CodeSpaceDbContext>()),
            scope.Resolve<ISandboxRunnerRegistry>(),
            new FixedWorkspaceResolver(),
            scope.Resolve<IModelCredentialResolver>(),
            new SingleProviderRegistry(provider),
            scope.Resolve<IAgentRunCompletionNotifier>(),
            scope.Resolve<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            scope.Resolve<CodeSpaceDbContext>(),
            scope.Resolve<CodeSpace.Core.Services.Review.IStructuredCritic>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(),
            NullLogger<AgentRunExecutor>.Instance);

        await executor.ExecuteAsync(runId, cancellationToken);
    }

    /// <summary>Always returns a request, so the executor materialises a workspace — the fake provider ignores its contents.</summary>
    private sealed class FixedWorkspaceResolver : IAgentWorkspaceResolver
    {
        public Task<WorkspaceProvisionRequest?> ResolveAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkspaceProvisionRequest?>(WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = "file:///dev/null" }));

        public Task<WorkspaceRequest?> ResolveByRepositoryIdAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken, string? @ref = null, bool softFallback = false) =>
            Task.FromResult<WorkspaceRequest?>(new WorkspaceRequest { RepositoryUrl = "file:///dev/null" });
    }

    private sealed class SingleProviderRegistry : IWorkspaceProviderRegistry
    {
        private readonly IWorkspaceProvider _provider;
        public SingleProviderRegistry(IWorkspaceProvider provider) { _provider = provider; }
        public IReadOnlyList<IWorkspaceProvider> All => new[] { _provider };
        public IWorkspaceProvider Resolve(string kind) => _provider;
    }

    /// <summary>Prepares a REAL temp directory (so on-disk survival is observable) and records whether its handle was disposed.</summary>
    private sealed class RecordingWorkspaceProvider : IWorkspaceProvider
    {
        public string? PreparedDirectory { get; private set; }
        public bool Disposed { get; private set; }

        public string Kind => LocalProcessRunner.LocalKind;

        public Task<IWorkspaceHandle> PrepareAsync(WorkspaceProvisionRequest request, CancellationToken cancellationToken)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cs-ws-reattach-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            PreparedDirectory = dir;
            return Task.FromResult<IWorkspaceHandle>(new Handle(this, dir));
        }

        private sealed class Handle : IWorkspaceHandle
        {
            private readonly RecordingWorkspaceProvider _owner;
            public Handle(RecordingWorkspaceProvider owner, string directory) { _owner = owner; Directory = directory; }
            public string Directory { get; }
            public string PrimaryAlias => "repo";
            public IReadOnlyList<WorkspaceRepositoryHandle> Repositories => new[] { new WorkspaceRepositoryHandle { Alias = "repo", Directory = Directory, Access = WorkspaceAccess.Write } };
            public Task<WorkspaceChanges> CaptureChangesAsync(CancellationToken cancellationToken) => Task.FromResult(new WorkspaceChanges { Patch = "" });
            public Task<WorkspaceChanges> CaptureChangesAsync(string alias, CancellationToken cancellationToken) => CaptureChangesAsync(cancellationToken);
            public ValueTask DisposeAsync()
            {
                _owner.Disposed = true;
                try { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true); } catch { /* best-effort */ }
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>A configurable multi-repo workspace whose per-alias capture returns distinct, deterministic changes and whose push (it IS an <see cref="IWorkspacePushHandle"/>) echoes the branch — drives the executor's multi-repo Enrich + Push paths without real git. A capture can be made to throw to exercise per-repo isolation.</summary>
    private sealed class MultiRepoRecordingWorkspaceProvider : IWorkspaceProvider
    {
        private readonly IReadOnlyList<(string alias, WorkspaceAccess access, bool primary)> _repos;
        private readonly HashSet<string> _throwCaptureFor;

        public MultiRepoRecordingWorkspaceProvider(IReadOnlyList<(string alias, WorkspaceAccess access, bool primary)>? repos = null, HashSet<string>? throwCaptureFor = null)
        {
            _repos = repos ?? new[] { ("web", WorkspaceAccess.Write, true), ("api", WorkspaceAccess.Write, false) };
            _throwCaptureFor = throwCaptureFor ?? new HashSet<string>();
        }

        public string Kind => LocalProcessRunner.LocalKind;

        public Task<IWorkspaceHandle> PrepareAsync(WorkspaceProvisionRequest request, CancellationToken cancellationToken)
        {
            var root = Path.Combine(Path.GetTempPath(), "cs-multirepo-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return Task.FromResult<IWorkspaceHandle>(new Handle(root, _repos, _throwCaptureFor));
        }

        private sealed class Handle : IWorkspaceHandle, IWorkspacePushHandle
        {
            private readonly string _root;
            private readonly IReadOnlyList<(string alias, WorkspaceAccess access, bool primary)> _repos;
            private readonly HashSet<string> _throwCaptureFor;

            public Handle(string root, IReadOnlyList<(string alias, WorkspaceAccess access, bool primary)> repos, HashSet<string> throwCaptureFor)
            {
                _root = root; Directory = root; _repos = repos; _throwCaptureFor = throwCaptureFor;
            }

            public string Directory { get; }
            public string PrimaryAlias => _repos.First(r => r.primary).alias;

            public IReadOnlyList<WorkspaceRepositoryHandle> Repositories =>
                _repos.Select(r => new WorkspaceRepositoryHandle { Alias = r.alias, Directory = Path.Combine(_root, r.alias), Access = r.access, BaseBranch = $"main-{r.alias}" }).ToList();

            public Task<WorkspaceChanges> CaptureChangesAsync(CancellationToken cancellationToken) => CaptureChangesAsync(PrimaryAlias, cancellationToken);

            public Task<WorkspaceChanges> CaptureChangesAsync(string alias, CancellationToken cancellationToken)
            {
                if (_throwCaptureFor.Contains(alias)) throw new WorkspaceException($"simulated capture failure for '{alias}'");

                return Task.FromResult(new WorkspaceChanges { BaseSha = $"base-{alias}", Patch = $"diff for {alias}", ChangedFiles = new[] { $"{alias}.txt" } });
            }

            // Each writable repo "pushes" by echoing the branch (records nothing — the executor folds it into the result).
            public Task<string?> PushChangesAsync(string alias, string branchName, CancellationToken cancellationToken) => Task.FromResult<string?>(branchName);
            public Task<string?> PushChangesAsync(string branchName, CancellationToken cancellationToken) => Task.FromResult<string?>(branchName);

            public ValueTask DisposeAsync()
            {
                try { if (System.IO.Directory.Exists(_root)) System.IO.Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
                return ValueTask.CompletedTask;
            }
        }
    }

    private async Task ExecuteAsync(Guid runId, IAgentHarness harness)
    {
        using var scope = _fixture.BeginScope();
        var executor = new AgentRunExecutor(
            scope.Resolve<IAgentRunService>(),
            new AgentHarnessRegistry(new[] { harness }),
            new HarnessModelReconciler(new AgentHarnessRegistry(new[] { harness }), scope.Resolve<IModelPoolSelector>(), scope.Resolve<CodeSpaceDbContext>()),
            scope.Resolve<ISandboxRunnerRegistry>(),
            scope.Resolve<IAgentWorkspaceResolver>(),
            scope.Resolve<IModelCredentialResolver>(),
            scope.Resolve<IWorkspaceProviderRegistry>(),
            scope.Resolve<IAgentRunCompletionNotifier>(),
            scope.Resolve<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            scope.Resolve<CodeSpaceDbContext>(),
            scope.Resolve<CodeSpace.Core.Services.Review.IStructuredCritic>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(),
            NullLogger<AgentRunExecutor>.Instance);

        await executor.ExecuteAsync(runId, CancellationToken.None);
    }

    /// <summary>
    /// Drives the executor with the real services EXCEPT IAgentRunService is wrapped in <see cref="InstrumentedAgentRunService"/>
    /// (passed as the FIRST ctor arg, so the BufferedEventWriter writes through it) — counting batched/per-event appends and,
    /// when <paramref name="throwOnAppendEventsCall"/> &gt; 0, faulting the Nth batched flush. Returns the decorator so the test
    /// can assert its counters. The injected service shares the scope's DbContext, so all reads/writes stay consistent.
    /// </summary>
    private async Task<InstrumentedAgentRunService> ExecuteInstrumentedAsync(Guid runId, IAgentHarness harness, int throwOnAppendEventsCall = 0)
    {
        using var scope = _fixture.BeginScope();
        var instrumented = new InstrumentedAgentRunService(scope.Resolve<IAgentRunService>()) { ThrowOnAppendEventsCall = throwOnAppendEventsCall };

        var executor = new AgentRunExecutor(
            instrumented,
            new AgentHarnessRegistry(new[] { harness }),
            new HarnessModelReconciler(new AgentHarnessRegistry(new[] { harness }), scope.Resolve<IModelPoolSelector>(), scope.Resolve<CodeSpaceDbContext>()),
            scope.Resolve<ISandboxRunnerRegistry>(),
            scope.Resolve<IAgentWorkspaceResolver>(),
            scope.Resolve<IModelCredentialResolver>(),
            scope.Resolve<IWorkspaceProviderRegistry>(),
            scope.Resolve<IAgentRunCompletionNotifier>(),
            scope.Resolve<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            scope.Resolve<CodeSpaceDbContext>(),
            scope.Resolve<CodeSpace.Core.Services.Review.IStructuredCritic>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(),
            NullLogger<AgentRunExecutor>.Instance);

        await executor.ExecuteAsync(runId, CancellationToken.None);
        return instrumented;
    }

    private async Task<Guid> CreateScriptedRunAsync(Guid teamId, int timeoutSeconds = 1800)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "scripted", Harness = "scripted", Model = "test-model", TimeoutSeconds = timeoutSeconds },
            teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);
        return run.Id;
    }

    /// <summary>Create a run whose task carries a multi-repo <see cref="WorkspaceSpec"/> (so the executor can resolve each per-repo result's RepositoryId from the authoring spec), optionally opting into branch push.</summary>
    private async Task<Guid> CreateMultiRepoRunAsync(Guid teamId, bool push, params (string alias, Guid repoId, WorkspaceAccess access, bool primary)[] repos)
    {
        using var scope = _fixture.BeginScope();

        var spec = new WorkspaceSpec
        {
            PrimaryAlias = repos.First(r => r.primary).alias,
            Repositories = repos.Select(r => new WorkspaceRepositorySpec { Alias = r.alias, RepositoryId = r.repoId, Access = r.access, IsPrimary = r.primary }).ToList(),
        };

        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "scripted", Harness = "scripted", Model = "test-model", TimeoutSeconds = 1800, Workspace = spec, PushProducedBranch = push },
            teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);
        return run.Id;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"agent-{userId:N}@test.local", Name = $"agent-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"agent-{teamId:N}", Name = "Agent Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }

    /// <summary>A CLI-less test harness: builds a /bin/sh invocation from a fixed script, wraps each stdout line as an assistant message, and folds the exit code.</summary>
    private sealed class ScriptedHarness : IAgentHarness
    {
        private readonly string _script;

        public ScriptedHarness(string script) => _script = script;

        public string Kind => "scripted";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "/bin/sh", Args = new[] { "-c", _script }, WorkingDirectory = task.WorkspaceDirectory, TimeoutSeconds = task.TimeoutSeconds };

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
            string.IsNullOrWhiteSpace(rawLine) ? Array.Empty<AgentEvent>() : new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine.Trim() } };

        public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode) =>
            exitCode == 0
                ? new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = events.Count > 0 ? events[^1].Text : null }
                : new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = $"exit {exitCode}" };
    }

    /// <summary>A scripted harness (kind "scripted") whose ParseEvent attaches the raw JSON as Data and whose BuildResult reads token usage off the events via <see cref="AgentTokenUsageReader"/> — exactly as the real Codex/Claude adapters do — so the executor→persist path for AgentRunResult.TokenUsage is exercised end-to-end.</summary>
    private sealed class UsageReportingHarness : IAgentHarness
    {
        private readonly string _script;

        public UsageReportingHarness(string script) => _script = script;

        public string Kind => "scripted";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "/bin/sh", Args = new[] { "-c", _script }, WorkingDirectory = task.WorkspaceDirectory, TimeoutSeconds = task.TimeoutSeconds };

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) return Array.Empty<AgentEvent>();

            if (line.StartsWith('{'))
                try { using var doc = JsonDocument.Parse(line); return new[] { new AgentEvent { Kind = AgentEventKind.Warning, Text = "usage", Data = doc.RootElement.Clone() } }; }
                catch (JsonException) { /* fall through to plain text */ }

            return new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = line } };
        }

        public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode) =>
            new()
            {
                Status = exitCode == 0 ? AgentRunStatus.Succeeded : AgentRunStatus.Failed,
                ExitReason = exitCode == 0 ? "completed" : "non-zero-exit",
                Summary = events.LastOrDefault(e => e.Kind == AgentEventKind.AssistantMessage)?.Text,
                TokenUsage = AgentTokenUsageReader.TryRead(events),
            };
    }

    /// <summary>A scripted harness that ALSO projects a model credential (kind "scripted-projector"): maps the resolved credential to one env var and — critically — carries <c>task.Environment</c> (the injected secret) into the sandbox spec, exactly as the real harnesses do.</summary>
    private sealed class ProjectingScriptedHarness : IAgentHarness, IModelCredentialProjector
    {
        private readonly string _provider;
        private readonly string _envVar;
        private readonly string _script;

        public ProjectingScriptedHarness(string provider, string envVar, string script)
        {
            _provider = provider;
            _envVar = envVar;
            _script = script;
        }

        public string Kind => "scripted-projector";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "/bin/sh", Args = new[] { "-c", _script }, WorkingDirectory = task.WorkspaceDirectory, Environment = task.Environment, TimeoutSeconds = task.TimeoutSeconds };

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
            string.IsNullOrWhiteSpace(rawLine) ? Array.Empty<AgentEvent>() : new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine.Trim() } };

        public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode) =>
            exitCode == 0
                ? new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = events.Count > 0 ? events[^1].Text : null }
                : new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = $"exit {exitCode}" };

        public IReadOnlyList<string> SupportedProviders => new[] { _provider };

        public IReadOnlyDictionary<string, string> ProjectToEnv(ResolvedModelCredential credential) =>
            new Dictionary<string, string> { [_envVar] = credential.ApiKey ?? "" };
    }

    /// <summary>A projecting harness whose script ECHOES its injected key to stdout, and whose events carry that line in BOTH Text and a structured Data payload — the leak the redactor must mask before the append-only log persists it.</summary>
    private sealed class KeyEchoingHarness : IAgentHarness, IModelCredentialProjector
    {
        private readonly string _provider;
        private readonly string _envVar;

        public KeyEchoingHarness(string provider, string envVar)
        {
            _provider = provider;
            _envVar = envVar;
        }

        public string Kind => "scripted-projector";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "/bin/sh", Args = new[] { "-c", $"echo \"${_envVar}\"" }, WorkingDirectory = task.WorkspaceDirectory, Environment = task.Environment, TimeoutSeconds = task.TimeoutSeconds };

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine)
        {
            var line = rawLine.Trim();
            return line.Length == 0 ? Array.Empty<AgentEvent>() : new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = line, Data = JsonSerializer.SerializeToElement(new { line }) } };
        }

        public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode) =>
            new() { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = events.Count > 0 ? events[^1].Text : null };

        public IReadOnlyList<string> SupportedProviders => new[] { _provider };

        public IReadOnlyDictionary<string, string> ProjectToEnv(ResolvedModelCredential credential) =>
            new Dictionary<string, string> { [_envVar] = credential.ApiKey ?? "" };
    }
}

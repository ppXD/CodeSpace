using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using System.Diagnostics;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The S2 live-re-attach path (Rule 12 high-fidelity): a REAL detached durable supervisor outlives its
/// observer (a backend restart), and the system resumes it WITHOUT losing or duplicating its timeline.
/// Covers the four things that make re-attach trustworthy:
///   1. a fresh observer resumes from the persisted checkpoint offset, so the timeline is continuous and
///      every line appears EXACTLY ONCE (no re-emit of the dead observer's prefix);
///   2. the resumed tail is REDACTED — ReattachAsync re-resolves the credential to rebuild the redactor,
///      never freezing an echoed secret into the append-only log;
///   3. the reconciler atomically re-claims (epoch bump + re-lease) a stale-but-alive run and dispatches
///      ReattachAsync — fencing a revived original observer out of completion;
///   4. re-attach attempts are BOUNDED — a permanently-unattachable-but-alive run is abandoned, never
///      reclaimed forever.
/// Real process + real Postgres + real services resolved through CodeSpaceModule (no mocks). POSIX-only.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentRunReattachFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly Dictionary<Guid, AgentRunReattachReservation> _reservations = new();
    private readonly List<int> _pidsToKill = new();
    private readonly List<string> _spoolDirs = new();

    public AgentRunReattachFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Re_attaches_a_live_run_resuming_from_the_checkpoint_with_no_duplicate_events()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

        // A REAL detached supervisor emits 6 lines over ~3s. A dead observer consumes the first few — persisting
        // their events + CHECKPOINTING the offset — then is torn down (the backend restart) WITHOUT killing it.
        var spec = new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "for i in 1 2 3 4 5 6; do echo step$i; sleep 0.5; done" }, TimeoutSeconds = 60 };

        using (var scope = _fixture.BeginScope())
        {
            var runner = (ISandboxDurableRunner)scope.Resolve<ISandboxRunnerRegistry>().Resolve(LocalProcessRunner.LocalKind);
            var svc = scope.Resolve<IAgentRunService>();

            var handle = await runner.LaunchAsync(spec, runId.ToString("N"), CancellationToken.None);
            _pidsToKill.Add(handle.ProcessId);
            _spoolDirs.Add(handle.SpoolDirectory);
            await svc.SetRunnerHandleAsync(runId, JsonSerializer.Serialize(handle, AgentJson.Options), CancellationToken.None);

            using var deadCts = new CancellationTokenSource();
            var persisted = 0;
            await Should.ThrowAsync<OperationCanceledException>(() => runner.AttachAsync(handle,
                async (frame, _) =>
                {
                    await svc.AppendEventAsync(runId, new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = frame.Text.Trim() }, CancellationToken.None);
                    persisted++;
                },
                deadCts.Token,
                async (offset, _) =>
                {
                    await svc.SetRunnerHandleAsync(runId, JsonSerializer.Serialize(handle with { StdoutOffset = offset }, AgentJson.Options), CancellationToken.None);
                    if (persisted >= 3) deadCts.Cancel();   // cancel AFTER the checkpoint persisted → clean teardown, no overlap
                }));
        }

        // The worker vanished without completing: still Running, with a mid-stream checkpoint and a partial log.
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            var run = await svc.GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.Running);
            JsonSerializer.Deserialize<SandboxHandle>(run.RunnerHandleJson!, AgentJson.Options)!.StdoutOffset
                .ShouldBeGreaterThan(0, "the dead observer checkpointed a mid-stream offset");
            (await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None)).Count.ShouldBeGreaterThanOrEqualTo(3);
        }

        // Reclaim (the reconciler's atomic step) then re-attach: ReattachAsync resumes from the checkpoint, reads
        // the remaining lines + the exit marker, and completes — under the reclaim-bumped epoch.
        using (var scope = _fixture.BeginScope())
        {
            // The original observer has stopped; advance the persisted lease into the expired state.
            await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() - interval '1 hour' WHERE id = {runId}");
            _reservations[runId] = (await scope.Resolve<IAgentRunService>().ReserveReattachAsync(runId, CancellationToken.None))!;
            _reservations[runId].ShouldNotBeNull();
        }

        var capture = new RecordingLogCaptureBridge();
        await ReattachAsync(runId, new ScriptedHarness(), capture);

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            var run = await svc.GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.Succeeded, "the re-attached observer tailed the live process to completion");
            var persistedHandle = JsonSerializer.Deserialize<SandboxHandle>(run.RunnerHandleJson!, AgentJson.Options)!;
            persistedHandle.AgentRunLogCaptureSessionId.ShouldBe(capture.OpenRequests.Single().Handle.AgentRunLogCaptureSessionId,
                "the legacy reattach handle is durable before the log observer starts");

            (await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None)).Select(e => e.Text)
                .ShouldBe(new[] { "step1", "step2", "step3", "step4", "step5", "step6" }, "the timeline is continuous + each line appears EXACTLY ONCE (resume skipped the already-emitted prefix)");
        }
        capture.OpenRequests.Count.ShouldBe(1);
        capture.OpenRequests[0].Handle.AgentRunLogCaptureSessionId.ShouldNotBeNull("a legacy handle is stamped and persisted before reattach observation");
        capture.OpenRequests[0].Source.ShouldBeAssignableTo<ISandboxDurableLogSource>();
        capture.Completions.ShouldBe(new[] { (teamId, runId, 2L) });
    }

    [Fact]
    public async Task Crash_between_a_batch_flush_and_the_offset_persist_re_emits_exactly_the_uncheckpointed_batch()
    {
        if (OperatingSystem.IsWindows()) return;

        // D1 bounded crash re-delivery (the documented "at worst re-emits the last batch, never loses a line"
        // floor). A dead observer FLUSHES a batch (committing it) then crashes BEFORE persisting the advanced
        // offset — exactly the gap the flush-before-offset ordering leaves open. On reattach the durable offset is
        // still behind that batch, so the re-tail re-emits it. We pin: NO line lost, and the duplicate set is
        // EXACTLY the un-checkpointed batch (not the whole prefix → unbounded growth, not zero → loss), each
        // duplicate a NEW append (greater Sequence), never a rewrite.
        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

        var spec = new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "printf 'step1\\nstep2\\nstep3\\n'; sleep 3; printf 'step4\\nstep5\\nstep6\\n'" }, TimeoutSeconds = 60 };

        var flushedTexts = new List<string>();   // whatever the first non-empty checkpoint flushed (the un-checkpointed batch)
        using (var scope = _fixture.BeginScope())
        {
            var runner = (ISandboxDurableRunner)scope.Resolve<ISandboxRunnerRegistry>().Resolve(LocalProcessRunner.LocalKind);
            var svc = scope.Resolve<IAgentRunService>();

            var handle = await runner.LaunchAsync(spec, runId.ToString("N"), CancellationToken.None);
            _pidsToKill.Add(handle.ProcessId);
            _spoolDirs.Add(handle.SpoolDirectory);
            await svc.SetRunnerHandleAsync(runId, JsonSerializer.Serialize(handle, AgentJson.Options), CancellationToken.None);   // StdoutOffset = 0

            var buffered = new List<AgentEvent>();
            await Should.ThrowAsync<CrashSimulation>(() => runner.AttachAsync(handle,
                (frame, _) => { buffered.Add(new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = frame.Text.Trim() }); return Task.CompletedTask; },
                CancellationToken.None,
                async (_, _) =>
                {
                    if (buffered.Count == 0) return;   // wait for a checkpoint that actually carries a batch

                    flushedTexts = buffered.Select(e => e.Text).ToList();
                    await svc.AppendEventsAsync(runId, buffered.ToList(), CancellationToken.None);   // the batch COMMITS
                    buffered.Clear();
                    throw new CrashSimulation();        // ... then crash BEFORE SetRunnerHandleAsync → the offset never advances
                }));
        }

        flushedTexts.ShouldNotBeEmpty("the dead observer flushed at least one line before crashing");

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            (await svc.GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Running);
            JsonSerializer.Deserialize<SandboxHandle>((await svc.GetAsync(runId, CancellationToken.None)).RunnerHandleJson!, AgentJson.Options)!.StdoutOffset
                .ShouldBe(0, "the crash hit AFTER the flush committed but BEFORE the offset advanced");
            (await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None)).Select(e => e.Text).ShouldBe(flushedTexts, "exactly the flushed batch is durable so far");
        }

        using (var scope = _fixture.BeginScope())
        {
            // The original observer has stopped; advance the persisted lease into the expired state.
            await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() - interval '1 hour' WHERE id = {runId}");
            _reservations[runId] = (await scope.Resolve<IAgentRunService>().ReserveReattachAsync(runId, CancellationToken.None))!;
            _reservations[runId].ShouldNotBeNull();
        }

        await ReattachAsync(runId, new ScriptedHarness());

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            (await svc.GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded, "the re-attached observer tailed the live process to completion");

            var events = await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None);
            var texts = events.Select(e => e.Text).ToList();
            var allSteps = new[] { "step1", "step2", "step3", "step4", "step5", "step6" };

            foreach (var s in allSteps) texts.ShouldContain(s, $"{s} is never lost");
            texts.Count.ShouldBe(allSteps.Length + flushedTexts.Count, "exactly the un-checkpointed batch is re-emitted — nothing more (no whole-prefix re-emit), nothing less (no loss)");

            foreach (var s in allSteps)
                texts.Count(t => t == s).ShouldBe(flushedTexts.Contains(s) ? 2 : 1, $"{s} appears {(flushedTexts.Contains(s) ? "twice (flushed-then-re-emitted)" : "once")}");

            foreach (var s in flushedTexts)
            {
                var seqs = events.Where(e => e.Text == s).Select(e => e.Sequence).OrderBy(x => x).ToList();
                seqs[1].ShouldBeGreaterThan(seqs[0], $"the re-emitted {s} is a NEW append (greater Sequence), not a rewrite of the append-only log");
            }
        }
    }

    [Fact]
    public async Task A_transient_flush_failure_that_commits_nothing_recovers_a_complete_gap_free_log_on_reattach()
    {
        if (OperatingSystem.IsWindows()) return;

        // The RECOVERY half of the flush-before-offset contract: when a checkpoint flush FAILS before committing
        // (a transient DB blip), nothing is persisted AND the offset never advanced — so reattach re-tails from 0
        // and reconstructs the FULL log exactly once. A transient flush failure must cost AT MOST a re-emit of the
        // un-checkpointed batch, never a permanent hole.
        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

        var spec = new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "printf 'step1\\nstep2\\nstep3\\n'; sleep 3; printf 'step4\\nstep5\\nstep6\\n'" }, TimeoutSeconds = 60 };

        using (var scope = _fixture.BeginScope())
        {
            var runner = (ISandboxDurableRunner)scope.Resolve<ISandboxRunnerRegistry>().Resolve(LocalProcessRunner.LocalKind);
            var svc = scope.Resolve<IAgentRunService>();

            var handle = await runner.LaunchAsync(spec, runId.ToString("N"), CancellationToken.None);
            _pidsToKill.Add(handle.ProcessId);
            _spoolDirs.Add(handle.SpoolDirectory);
            await svc.SetRunnerHandleAsync(runId, JsonSerializer.Serialize(handle, AgentJson.Options), CancellationToken.None);

            var sawLine = false;
            await Should.ThrowAsync<CrashSimulation>(() => runner.AttachAsync(handle,
                (_, _) => { sawLine = true; return Task.CompletedTask; },
                CancellationToken.None,
                (_, _) => sawLine ? throw new CrashSimulation() : Task.CompletedTask));   // the flush throws BEFORE committing anything
        }

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            (await svc.GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Running);
            JsonSerializer.Deserialize<SandboxHandle>((await svc.GetAsync(runId, CancellationToken.None)).RunnerHandleJson!, AgentJson.Options)!.StdoutOffset.ShouldBe(0);
            (await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None)).ShouldBeEmpty("the failed flush committed nothing");
        }

        using (var scope = _fixture.BeginScope())
        {
            // The original observer has stopped; advance the persisted lease into the expired state.
            await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() - interval '1 hour' WHERE id = {runId}");
            _reservations[runId] = (await scope.Resolve<IAgentRunService>().ReserveReattachAsync(runId, CancellationToken.None))!;
            _reservations[runId].ShouldNotBeNull();
        }

        await ReattachAsync(runId, new ScriptedHarness());

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            (await svc.GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded);

            var events = await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None);
            events.Select(e => e.Text).ShouldBe(new[] { "step1", "step2", "step3", "step4", "step5", "step6" }, "the full log is recovered exactly once — no gap, no duplicate");
            events.Select(e => e.Sequence).SequenceEqual(events.Select(e => e.Sequence).OrderBy(s => s)).ShouldBeTrue("sequences strictly ascending");
        }
    }

    [Fact]
    public async Task Reattach_drains_exactly_the_post_offset_tail_via_the_final_flush_never_the_whole_spool_nor_zero()
    {
        if (OperatingSystem.IsWindows()) return;

        // D1 terminal-drain final-flush floor. The terminal drain has NO trailing checkpoint, so the executor's
        // FINAL FlushAsync (after AttachAsync returns) is the ONLY thing that persists the last batch. A regression
        // that dropped the final flush would silently TRUNCATE the tail of EVERY run while it still completes
        // Succeeded. We pre-stage a fully-emitted, exited spool with the dead observer's checkpoint at offset O
        // (after step3): reattach must append EXACTLY [O,end) = step4..step6 — never the whole spool (a re-emit
        // from 0), never zero (a dropped final flush).
        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

        var spoolDir = NewSpoolDir();
        const string prefix = "step1\nstep2\nstep3\n";
        await File.WriteAllTextAsync(Path.Combine(spoolDir, "out.log"), prefix + "step4\nstep5\nstep6\n");
        await File.WriteAllTextAsync(Path.Combine(spoolDir, "exit"), "0");

        var handle = new SandboxHandle { Kind = "local", ProcessId = 2147480010, SpoolDirectory = spoolDir, Deadline = DateTimeOffset.UtcNow.AddMinutes(10), StdoutOffset = prefix.Length };
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().SetRunnerHandleAsync(runId, JsonSerializer.Serialize(handle, AgentJson.Options), CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            // The original observer has stopped; advance the persisted lease into the expired state.
            await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() - interval '1 hour' WHERE id = {runId}");
            _reservations[runId] = (await scope.Resolve<IAgentRunService>().ReserveReattachAsync(runId, CancellationToken.None))!;
            _reservations[runId].ShouldNotBeNull();
        }

        await ReattachAsync(runId, new ScriptedHarness());

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            var run = await svc.GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.Succeeded, "the exit marker said 0");

            var events = await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None);
            events.Select(e => e.Text).ShouldBe(new[] { "step4", "step5", "step6" }, "the terminal drain re-emitted EXACTLY [O,end) — the final flush captured the un-checkpointed tail, and the pre-offset prefix was NOT re-read");
            events.Select(e => e.Sequence).SequenceEqual(events.Select(e => e.Sequence).OrderBy(s => s)).ShouldBeTrue("the drain batch's sequences are contiguous + ascending (one ordered flush)");

            // D3a: the reattach path captures its OWN transcript, scoped to the RESUMED tail [O,end). A small tail
            // stays inline. It must carry exactly the post-offset lines (step4..step6) and NOT the pre-crash prefix
            // (step1..step3) — that prefix lived in the dead observer's process and is never re-read into the tail.
            var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
            result.TranscriptArtifactId.ShouldBeNull("a tiny resumed-tail transcript stays inline");
            var lines = result.Transcript.Split('\n');
            lines.ShouldContain("step4");
            lines.ShouldContain("step6");
            result.Transcript.ShouldNotContain("step1", customMessage: "the reattach transcript is the resumed TAIL only — the pre-offset prefix is not in it (documented tail-only contract)");
        }
    }

    [Fact]
    public async Task A_fully_checkpointed_reattach_is_a_log_no_op()
    {
        if (OperatingSystem.IsWindows()) return;

        // D1 upper bound on re-delivery: a reattach whose persisted offset is ALREADY at the full spool length must
        // re-read NOTHING — appending zero events while completing cleanly. Catches the stale-handle / resume-from-0
        // bug that would balloon the log by the whole spool on every reattach.
        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

        var spoolDir = NewSpoolDir();
        const string allOutput = "line1\nline2\nline3\n";
        await File.WriteAllTextAsync(Path.Combine(spoolDir, "out.log"), allOutput);
        await File.WriteAllTextAsync(Path.Combine(spoolDir, "exit"), "0");

        var handle = new SandboxHandle { Kind = "local", ProcessId = 2147480011, SpoolDirectory = spoolDir, Deadline = DateTimeOffset.UtcNow.AddMinutes(10), StdoutOffset = allOutput.Length };
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().SetRunnerHandleAsync(runId, JsonSerializer.Serialize(handle, AgentJson.Options), CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            // The original observer has stopped; advance the persisted lease into the expired state.
            await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() - interval '1 hour' WHERE id = {runId}");
            _reservations[runId] = (await scope.Resolve<IAgentRunService>().ReserveReattachAsync(runId, CancellationToken.None))!;
            _reservations[runId].ShouldNotBeNull();
        }

        await ReattachAsync(runId, new ScriptedHarness());

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            (await svc.GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded);
            (await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None)).ShouldBeEmpty("the offset was already at the spool end → the reattach re-read nothing (a log no-op)");
        }
    }

    [Fact]
    public async Task Re_attach_redacts_an_echoed_model_key_in_the_resumed_tail()
    {
        if (OperatingSystem.IsWindows()) return;

        const string key = "sk-reattach-leak-7c1f2e";

        var teamId = await SeedTeamAsync();
        var credId = await SeedModelCredentialAsync(teamId, "scripted-provider", key);
        var runId = await CreateRunWithCredentialAsync(teamId, credId);
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

        // Simulate a finished supervisor whose spool tail ECHOES the injected key (a CLI printing its key in a
        // banner / 401 body), with the dead observer's checkpoint set BEFORE that line — so the key line lands in
        // the RE-ATTACHED tail. ReattachAsync must re-resolve the credential to rebuild the redactor and mask the
        // key before the append-only log freezes it; it must NEVER re-tail with SecretRedactor.None.
        var spoolDir = NewSpoolDir();
        const string preCrash = "pre-crash-line\n";
        await File.WriteAllTextAsync(Path.Combine(spoolDir, "out.log"), preCrash + $"echoed key={key} here\npost-crash-line\n");
        await File.WriteAllTextAsync(Path.Combine(spoolDir, "exit"), "0");

        // The handle carries the fingerprint of the SAME key the run still resolves to, so the re-attach proves it
        // rebuilt the right redactor and re-tails (masking the echoed key) rather than falling back to marker-only.
        var handle = new SandboxHandle { Kind = "local", ProcessId = 2147480000, SpoolDirectory = spoolDir, Deadline = DateTimeOffset.UtcNow.AddMinutes(10), StdoutOffset = preCrash.Length, InjectedKeyFingerprint = new SecretRedactor(new[] { key }).Fingerprint };
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().SetRunnerHandleAsync(runId, JsonSerializer.Serialize(handle, AgentJson.Options), CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            // The original observer has stopped; advance the persisted lease into the expired state.
            await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() - interval '1 hour' WHERE id = {runId}");
            _reservations[runId] = (await scope.Resolve<IAgentRunService>().ReserveReattachAsync(runId, CancellationToken.None))!;
            _reservations[runId].ShouldNotBeNull();
        }

        await ReattachAsync(runId, new ProjectingHarness("scripted-provider", "SCRIPTED_MODEL_KEY"));

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            var run = await svc.GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.Succeeded, "the marker said exit 0");

            var events = await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None);
            events.ShouldNotBeEmpty();
            foreach (var e in events)
            {
                (e.Text ?? "").ShouldNotContain(key, customMessage: "the re-attached tail MUST be redacted — ReattachAsync re-resolved the credential, never re-tailing with SecretRedactor.None");
                (e.DataJson ?? "").ShouldNotContain(key);
            }
            events.ShouldContain(e => (e.Text ?? "").Contains(SecretRedactor.Placeholder), "the echoed key is masked, not silently dropped");
            run.ResultJson!.ShouldNotContain(key, customMessage: "the folded result must not carry the key either");
        }
    }

    [Fact]
    public async Task A_re_attached_brokered_run_records_that_its_model_access_died_with_the_minting_worker()
    {
        if (OperatingSystem.IsWindows()) return;

        const string brokerRunToken = "brokered-run-token-that-died-with-its-worker";

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

        var spoolDir = NewSpoolDir();
        await File.WriteAllTextAsync(Path.Combine(spoolDir, "out.log"), "resumed-line\n");
        await File.WriteAllTextAsync(Path.Combine(spoolDir, "exit"), "0");

        // The launch's posture as its runner stamped it — confined, brokered — plus the handle the launch left
        // behind, carrying the per-run bearer. The re-attach opens NO lease (the one that mattered lived in the dead
        // worker's memory), so the detached CLI is pointed at a port nothing answers on: its model access is over,
        // and the record has to say so WITHOUT losing what the launch recorded beside it.
        using (var scope = _fixture.BeginScope())
        {
            var runs = scope.Resolve<IAgentRunService>();
            await runs.SetSandboxConfinementAsync(runId, JsonSerializer.Serialize(new SandboxConfinement { Outcome = SandboxConfinementOutcome.Confined, NetworkSevered = true, ModelCredentialBrokered = true }, AgentJson.Options), CancellationToken.None);

            var fingerprint = AgentRunExecutor.WithModelBrokerRunToken(AgentRunExecutor.BuildRunRedactor(new Dictionary<string, string>(), null), brokerRunToken).Fingerprint;
            var handle = new SandboxHandle { Kind = "local", ProcessId = 2147480000, SpoolDirectory = spoolDir, Deadline = DateTimeOffset.UtcNow.AddMinutes(10), ModelBrokerRunToken = brokerRunToken, InjectedKeyFingerprint = fingerprint };
            await runs.SetRunnerHandleAsync(runId, JsonSerializer.Serialize(handle, AgentJson.Options), CancellationToken.None);
        }

        using (var scope = _fixture.BeginScope())
        {
            await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() - interval '1 hour' WHERE id = {runId}");
            _reservations[runId] = (await scope.Resolve<IAgentRunService>().ReserveReattachAsync(runId, CancellationToken.None))!;
        }

        await ReattachAsync(runId, new ScriptedHarness());

        using var verify = _fixture.BeginScope();
        var stored = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.SandboxConfinementJson).SingleAsync();
        var confinement = JsonSerializer.Deserialize<SandboxConfinement>(stored!, AgentJson.Options).ShouldNotBeNull();

        confinement.ModelCredentialLeaseLost.ShouldBeTrue(
            "a re-attach re-opens no lease, so every model call the detached agent makes from here fails to connect — unrecorded, that presents as a provider outage nobody can attribute");
        confinement.Outcome.ShouldBe(SandboxConfinementOutcome.Confined, "the fact is MERGED onto the launch's posture; replacing it would lose what the sandbox actually did");
        confinement.ModelCredentialBrokered.ShouldBe(true);

        AgentAutonomyPolicy.DescribeNetwork(AgentAutonomyLevel.Trusted, AgentAutonomyLevel.Trusted, AgentAutonomyLevel.Unleashed, confinement)
            .ShouldEndWith(AgentAutonomyPolicy.LostBrokeredModelCredentialCaveat,
                customMessage: "the recorded fact has to reach the sentence a reader actually sees, or it is a column nobody consults");
    }

    [Fact]
    public async Task Re_attach_does_NOT_re_tail_when_the_credential_no_longer_matches_the_launch_key()
    {
        if (OperatingSystem.IsWindows()) return;

        const string echoedOldKey = "sk-OLD-rotated-away-3b9c2e";
        const string currentKey = "sk-CURRENT-different-7a1d4f";

        var teamId = await SeedTeamAsync();
        var credId = await SeedModelCredentialAsync(teamId, "scripted-provider", currentKey);   // what the run resolves NOW
        var runId = await CreateRunWithCredentialAsync(teamId, credId);
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

        // The spool tail echoes the OLD key injected at launch; the handle's fingerprint is for THAT old key. At
        // re-attach the credential resolves to a DIFFERENT current key → fingerprint mismatch → ReattachAsync must
        // NOT re-tail (the rebuilt redactor could only mask the current key, never the old echoed one) → it
        // completes from the exit marker only, so the un-maskable old key is NEVER frozen into the log.
        var spoolDir = NewSpoolDir();
        await File.WriteAllTextAsync(Path.Combine(spoolDir, "out.log"), $"pre-crash\necho {echoedOldKey} leaked\npost-crash\n");
        await File.WriteAllTextAsync(Path.Combine(spoolDir, "exit"), "0");

        var handle = new SandboxHandle { Kind = "local", ProcessId = 2147480001, SpoolDirectory = spoolDir, Deadline = DateTimeOffset.UtcNow.AddMinutes(10), StdoutOffset = "pre-crash\n".Length, InjectedKeyFingerprint = new SecretRedactor(new[] { echoedOldKey }).Fingerprint };
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().SetRunnerHandleAsync(runId, JsonSerializer.Serialize(handle, AgentJson.Options), CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            // The original observer has stopped; advance the persisted lease into the expired state.
            await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() - interval '1 hour' WHERE id = {runId}");
            _reservations[runId] = (await scope.Resolve<IAgentRunService>().ReserveReattachAsync(runId, CancellationToken.None))!;
            _reservations[runId].ShouldNotBeNull();
        }

        await ReattachAsync(runId, new ProjectingHarness("scripted-provider", "SCRIPTED_MODEL_KEY"));

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            var run = await svc.GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.Succeeded, "the run completed from its exit marker only (exit 0)");

            (await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None))
                .ShouldBeEmpty("the mismatched-key tail was NOT re-emitted — the old echoed key never reached the append-only log");
            run.ResultJson!.ShouldNotContain(echoedOldKey);
            (run.Error ?? "").ShouldNotContain(echoedOldKey);
        }
    }

    [Fact]
    public async Task Reconciler_re_attaches_a_stale_but_alive_run_dispatching_the_executor()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        long claimedEpoch;
        using (var scope = _fixture.BeginScope())
            claimedEpoch = await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

        await LaunchAliveSupervisorAsync(runId);

        // No window override needed: the run has emitted no events, so it's already a stale candidate under the
        // real (5-min) liveness window once its lease lapses — and the reclaim then stamps a genuinely FUTURE lease
        // (a zeroed window would make the reclaim's lease = now, masking the re-lease behaviour we assert here).
        InMemoryBackgroundJobClient jobs;
        using (var scope = _fixture.BeginScope()) jobs = scope.Resolve<InMemoryBackgroundJobClient>();
        var originalAutoExecute = jobs.AutoExecute;

        try
        {
            jobs.AutoExecute = false;   // RECORD the dispatch only — never run the DI executor (its registry lacks the scripted harness)

            await LapseLeaseAsync(runId);

            AgentRunReconcileSummary summary;
            using (var scope = _fixture.BeginScope())
                summary = await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

            // >= not == : ReattachedStaleRunning is a deployment-wide bounded-batch tally (see
            // AgentRunReconcileSummary) — another test's stale row sharing this collection can legitimately land
            // in the same sweep. The FenceEpoch + LeaseExpiresAt + dispatched-call assertions below are what prove
            // THIS run was reattached.
            summary.ReattachedStaleRunning.ShouldBeGreaterThanOrEqualTo(1, "the reconciler re-attached the stale-but-alive run");
            jobs.Calls.ShouldContain(c => c.MethodName == nameof(IAgentRunExecutor.ReattachAsync) && c.RunId == runId, "it dispatched the executor's ReattachAsync for this run");

            using var verify = _fixture.BeginScope();
            var run = await verify.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            run.FenceEpoch.ShouldBe(claimedEpoch + 1, "the reclaim bumped the fence epoch");
            run.LeaseExpiresAt!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow, "the reclaim re-leased the run so it drops out of the stale sweep");
        }
        finally
        {
            jobs.AutoExecute = originalAutoExecute;
        }
    }

    [Fact]
    public async Task Re_attach_attempts_are_bounded_then_the_run_is_abandoned()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);
        long claimedEpoch;
        using (var scope = _fixture.BeginScope())
            claimedEpoch = await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

        await LaunchAliveSupervisorAsync(runId);

        var originalWindow = Environment.GetEnvironmentVariable(AgentRunReconcilerService.LivenessWindowEnvVar);
        InMemoryBackgroundJobClient jobs;
        using (var scope = _fixture.BeginScope()) jobs = scope.Resolve<InMemoryBackgroundJobClient>();
        var originalAutoExecute = jobs.AutoExecute;

        try
        {
            Environment.SetEnvironmentVariable(AgentRunReconcilerService.LivenessWindowEnvVar, "00:00:00");
            jobs.AutoExecute = false;   // the re-attach worker never actually runs (it "dies" before renewing the lease)

            // Each sweep re-attaches (within budget); we lapse the lease before each to model the worker dying.
            for (var attempt = 1; attempt <= AgentRunReconcilerService.MaxReattachAttempts; attempt++)
            {
                await LapseLeaseAsync(runId);
                using var scope = _fixture.BeginScope();

                // >= not == : ReattachedStaleRunning is a deployment-wide bounded-batch tally (see
                // AgentRunReconcileSummary) — another test's stale row sharing this collection can legitimately
                // land in the same sweep. THIS run's own FenceEpoch below (bumped by exactly 1 per reclaim) is
                // the proof it — not some other row — was reattached on sweep {attempt}.
                (await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None)).ReattachedStaleRunning
                    .ShouldBeGreaterThanOrEqualTo(1, $"sweep {attempt} re-attaches (still within the attempt budget)");

                (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).FenceEpoch
                    .ShouldBe(claimedEpoch + attempt, $"sweep {attempt}'s reclaim bumped THIS run's fence epoch by one — proof it was reattached, not merely tallied");
            }

            // Budget exhausted → the next sweep abandons, so a permanently-unattachable-but-alive run still
            // reaches a terminal state instead of being reclaimed forever.
            await LapseLeaseAsync(runId);
            using (var scope = _fixture.BeginScope())
                // >= not == : MarkedAbandonedFromRunning is the same deployment-wide tally; THIS run's Status flip
                // to Failed (asserted below) is what proves it — not just some other row — was abandoned.
                (await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None)).MarkedAbandonedFromRunning
                    .ShouldBeGreaterThanOrEqualTo(1, "past the re-attach budget the run is abandoned");

            using (var scope = _fixture.BeginScope())
                (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Failed);
        }
        finally
        {
            jobs.AutoExecute = originalAutoExecute;
            Environment.SetEnvironmentVariable(AgentRunReconcilerService.LivenessWindowEnvVar, originalWindow);
        }
    }

    /// <summary>Marks a deliberately-injected observer crash in a flush/checkpoint callback — distinct from any real failure so the test's Should.ThrowAsync can't be fooled.</summary>
    private sealed class CrashSimulation : Exception { }

    [Fact]
    public async Task Legacy_job_is_no_adopt_and_normal_reconciler_dispatches_a_serializable_one_time_job_for_the_historical_process()
    {
        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);
        var gateDirectory = NewSpoolDir();
        var gate = Path.Combine(gateDirectory, "release");
        SandboxHandle handle;
        using (var setup = _fixture.BeginScope())
        {
            var runs = setup.Resolve<IAgentRunService>();
            await runs.MarkRunningAsync(runId, CancellationToken.None);
            var runner = (ISandboxDurableRunner)setup.Resolve<ISandboxRunnerRegistry>().Resolve(LocalProcessRunner.LocalKind);
            handle = await runner.LaunchAsync(new SandboxSpec { Command = "/bin/sh", Args = ["-c", "echo started; while [ ! -f \"$1\" ]; do sleep 0.1; done; echo recovered", "ownership", gate], TimeoutSeconds = 30 }, runId.ToString("N"), CancellationToken.None);
            _pidsToKill.Add(handle.ProcessId);
            _spoolDirs.Add(handle.SpoolDirectory);
            await runs.SetRunnerHandleAsync(runId, JsonSerializer.Serialize(handle, AgentJson.Options), CancellationToken.None);
            await BuildExecutor(setup, new ScriptedHarness()).ReattachAsync(runId, CancellationToken.None);
            (await runs.GetAsync(runId, CancellationToken.None)).OwnerId.ShouldBeNull();
        }
        await LapseLeaseAsync(runId);
        using var dispatcher = _fixture.BeginScope();
        var jobs = dispatcher.Resolve<InMemoryBackgroundJobClient>();
        var autoExecute = jobs.AutoExecute;
        try
        {
            jobs.AutoExecute = false;
            await dispatcher.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);
            var call = jobs.Calls.Last(c => c.MethodName == nameof(IAgentRunExecutor.ReattachAsync) && c.RunId == runId);
            var reserved = call.FirstArgument.ShouldBeOfType<AgentRunReattachReservation>();
            var serialized = global::Hangfire.Storage.InvocationData.SerializeJob(global::Hangfire.Common.Job.FromExpression<IAgentRunExecutor>(e => e.ReattachAsync(reserved, CancellationToken.None)));
            var payload = serialized.DeserializeJob().Args[0].ShouldBeOfType<AgentRunReattachReservation>();
            payload.ShouldBe(reserved);
            _reservations[runId] = payload;
            var before = await dispatcher.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            await BuildExecutor(dispatcher, new ScriptedHarness()).ReattachAsync(runId, CancellationToken.None);
            var afterLegacy = await dispatcher.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            afterLegacy.OwnerId.ShouldBeNull();
            afterLegacy.ReattachReservationId.ShouldBe(before.ReattachReservationId);
            afterLegacy.LeaseExpiresAt.ShouldBe(before.LeaseExpiresAt);
            await File.WriteAllTextAsync(gate, "release the existing physical process");
            var captures = new RecordingLogCaptureBridge();
            await Task.WhenAll(ReattachAsync(runId, new ScriptedHarness(), captures), ReattachAsync(runId, new ScriptedHarness(), captures));
            captures.OpenRequests.Count.ShouldBe(1, "only the activated reservation may open the physical process for observation");
            var finished = await dispatcher.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            finished.Status.ShouldBe(AgentRunStatus.Succeeded);
            finished.FenceEpoch.ShouldBe(payload.Epoch);
            JsonSerializer.Deserialize<SandboxHandle>(finished.RunnerHandleJson!, AgentJson.Options)!.ProcessId.ShouldBe(handle.ProcessId);
            var rows = await dispatcher.Resolve<CodeSpaceDbContext>().AgentRunEvent.AsNoTracking().Where(e => e.AgentRunId == runId).ToListAsync();
            rows.Count(e => e.WriterKind == "worker" && e.Text == "recovered").ShouldBe(1);
            rows.Where(e => e.WriterKind == "worker").All(e => e.WriterOwnerId == finished.OwnerId && e.WriterEpoch == payload.Epoch).ShouldBeTrue();
            rows.ShouldContain(e => e.WriterKind == "system");
        }
        finally { jobs.AutoExecute = autoExecute; }
    }

    [Fact]
    public async Task Revived_original_observer_cannot_append_or_kill_successors_process_or_delete_its_workspace()
    {
        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);
        var gate = Path.Combine(NewSpoolDir(), "release");
        var harness = new ScriptedHarness($"echo started; while [ ! -f '{gate}' ]; do sleep 0.1; done; echo recovered");
        using var originalScope = _fixture.BeginScope();
        var reached = new TaskCompletionSource<AgentRunOwnerToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var instrumented = new InstrumentedAgentRunService(originalScope.Resolve<IAgentRunService>()) { BeforeOwnedAppendAsync = async owner => { reached.TrySetResult(owner); await resume.Task; } };
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var original = BuildExecutor(originalScope, harness, runs: instrumented).ExecuteAsync(runId, bounded.Token);
        Task? successor = null;
        try
        {
            var stale = await reached.Task.WaitAsync(bounded.Token);
            using var reclaim = _fixture.BeginScope();
            var runs = reclaim.Resolve<IAgentRunService>();
            var launched = await runs.GetAsync(runId, bounded.Token);
            var handle = JsonSerializer.Deserialize<SandboxHandle>(launched.RunnerHandleJson!, AgentJson.Options)!;
            _pidsToKill.Add(handle.ProcessId);
            _spoolDirs.Add(handle.SpoolDirectory);
            await LapseLeaseAsync(runId);
            _reservations[runId] = (await runs.ReserveReattachAsync(runId, bounded.Token))!;
            successor = ReattachAsync(runId, new ScriptedHarness());
            AgentRun current;
            do
            {
                current = await runs.GetAsync(runId, bounded.Token);
                if (current.OwnerId is null) await Task.Delay(20, bounded.Token);
            } while (current.OwnerId is null);
            current.OwnerId.ShouldNotBe(stale.OwnerId);
            resume.SetResult();
            await Should.ThrowAsync<CodeSpace.Core.Services.Agents.Exceptions.AgentRunOwnershipLostException>(() => original);
            Directory.Exists(harness.ObservedWorkspace).ShouldBeTrue("the losing observer cannot delete the shared workspace");
            var durable = (ISandboxDurableRunner)reclaim.Resolve<ISandboxRunnerRegistry>().Resolve(handle.Kind);
            (await durable.ProbeAsync(handle, bounded.Token)).State.ShouldBe(SandboxRunState.Running, "losing observation must not terminate the shared process");
            await File.WriteAllTextAsync(gate, "release", bounded.Token);
            await successor.WaitAsync(bounded.Token);
            var completed = await runs.GetAsync(runId, bounded.Token);
            completed.Status.ShouldBe(AgentRunStatus.Succeeded);
            completed.OwnerId.ShouldBe(current.OwnerId);
            var events = await reclaim.Resolve<CodeSpaceDbContext>().AgentRunEvent.AsNoTracking().Where(e => e.AgentRunId == runId).ToListAsync();
            events.ShouldNotContain(e => e.WriterOwnerId == stale.OwnerId);
            events.Count(e => e.Text == "recovered").ShouldBe(1);
        }
        finally
        {
            resume.TrySetResult();
            await File.WriteAllTextAsync(gate, "release");
            bounded.Cancel();
            try { await original; } catch { }
            if (successor != null) try { await successor.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            if (harness.ObservedWorkspace is { } workspace && Directory.Exists(workspace)) Directory.Delete(workspace, true);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────

    private async Task LaunchAliveSupervisorAsync(Guid runId)
    {
        // A real detached supervisor that stays alive (sleeping) → ProbeAsync sees Running → the re-attach branch.
        var spec = new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "echo started; sleep 60" }, TimeoutSeconds = 300 };

        using var scope = _fixture.BeginScope();
        var runner = (ISandboxDurableRunner)scope.Resolve<ISandboxRunnerRegistry>().Resolve(LocalProcessRunner.LocalKind);
        var handle = await runner.LaunchAsync(spec, runId.ToString("N"), CancellationToken.None);

        _pidsToKill.Add(handle.ProcessId);
        _spoolDirs.Add(handle.SpoolDirectory);
        await scope.Resolve<IAgentRunService>().SetRunnerHandleAsync(runId, JsonSerializer.Serialize(handle, AgentJson.Options), CancellationToken.None);
    }

    private async Task LapseLeaseAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().Database
            .ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = {DateTimeOffset.UtcNow.AddMinutes(-1)} WHERE id = {runId}");
    }

    private async Task ReattachAsync(Guid runId, IAgentHarness harness, IAgentRunLogCaptureBridge? logCapture = null)
    {
        using var scope = _fixture.BeginScope();
        await BuildExecutor(scope, harness, logCapture).ReattachAsync(_reservations[runId], CancellationToken.None);
    }

    private static AgentRunExecutor BuildExecutor(ILifetimeScope scope, IAgentHarness harness, IAgentRunLogCaptureBridge? logCapture = null, IAgentRunService? runs = null)
    {
        return new AgentRunExecutor(
            runs ?? scope.Resolve<IAgentRunService>(),
            new AgentHarnessRegistry(new[] { harness }),
            new HarnessModelReconciler(new AgentHarnessRegistry(new[] { harness }), scope.Resolve<IModelPoolSelector>(), scope.Resolve<CodeSpaceDbContext>()),
            scope.Resolve<ISandboxRunnerRegistry>(),
            scope.Resolve<IAgentWorkspaceResolver>(),
            scope.Resolve<IModelCredentialResolver>(),
            scope.Resolve<IWorkspaceProviderRegistry>(),
            scope.Resolve<IAgentRunCompletionNotifier>(),
            scope.Resolve<IServiceScopeFactory>(),
            scope.Resolve<CodeSpaceDbContext>(),
            scope.Resolve<CodeSpace.Core.Services.Review.IStructuredCritic>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactStore>(),
            scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IPublishManifestStore>(), scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IArtifactManifestStore>(), scope.Resolve<CodeSpace.Core.Services.Agents.Capture.ICaptureIntentService>(),
            scope.Resolve<IEnumerable<CodeSpace.Core.Services.Agents.Publish.IPublishGuard>>(),
            NullLogger<AgentRunExecutor>.Instance,
            logCapture);
    }

    private sealed class RecordingLogCaptureBridge : IAgentRunLogCaptureBridge
    {
        public List<AgentRunLogCaptureOpenRequest> OpenRequests { get; } = [];
        public List<(Guid TeamId, Guid AgentRunId, long WorkerFenceEpoch)> Completions { get; } = [];

        public Task<IAgentRunLogCaptureSession> OpenAsync(AgentRunLogCaptureOpenRequest request, CancellationToken cancellationToken)
        {
            OpenRequests.Add(request);
            return Task.FromResult<IAgentRunLogCaptureSession>(new Session(request.Handle));
        }

        public Task RecordGapAsync(AgentRunLogCaptureGapRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CompleteRunAsync(Guid teamId, Guid agentRunId, long workerFenceEpoch, CancellationToken cancellationToken)
        {
            Completions.Add((teamId, agentRunId, workerFenceEpoch));
            return Task.CompletedTask;
        }

        private sealed class Session(SandboxHandle handle) : IAgentRunLogCaptureSession
        {
            public SandboxHandle Handle { get; } = handle;
            public Task<SandboxResult> ObserveAsync(Func<SandboxHandle, CancellationToken, Task<SandboxResult>> observer, CancellationToken cancellationToken) => observer(Handle, cancellationToken);
        }
    }

    private string NewSpoolDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs-reattach-spool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _spoolDirs.Add(dir);
        return dir;
    }

    private async Task<Guid> CreateScriptedRunAsync(Guid teamId)
    {
        using var scope = await WorkflowsTestSeed.BeginSeedOperatorScopeAsync(_fixture, teamId);
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "scripted", Harness = "scripted", Model = "test-model", TimeoutSeconds = 1800 },
            teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);
        return run.Id;
    }

    private async Task<Guid> CreateRunWithCredentialAsync(Guid teamId, Guid modelCredentialId)
    {
        using var scope = await WorkflowsTestSeed.BeginSeedOperatorScopeAsync(_fixture, teamId);
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
            EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt(plaintextKey),
            Status = CredentialStatus.Active,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"agent-{userId:N}@test.local", Name = $"agent-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"agent-{teamId:N}", Name = "Agent Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }

    public void Dispose()
    {
        foreach (var pid in _pidsToKill)
            try { using var p = Process.GetProcessById(pid); if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best-effort */ }

        foreach (var dir in _spoolDirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>CLI-less harness whose ParseEvent wraps each stdout line as an assistant message — for re-attach, only ParseEvent + BuildResult are exercised (no launch).</summary>
    private sealed class ScriptedHarness(string script = "true") : IAgentHarness
    {
        public string Kind => "scripted";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public string? ObservedWorkspace { get; private set; }
        public SandboxSpec BuildInvocation(AgentTask task)
        {
            ObservedWorkspace = task.WorkspaceDirectory;
            return new() { Command = "/bin/sh", Args = ["-c", script], WorkingDirectory = task.WorkspaceDirectory, TimeoutSeconds = task.TimeoutSeconds };
        }

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
            string.IsNullOrWhiteSpace(rawLine) ? Array.Empty<AgentEvent>() : new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine.Trim() } };

        public IAgentEventFolder CreateFolder() => new TestEventFolder((fold, exitCode) =>
            exitCode == 0
                ? new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = fold.LastText }
                : new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = $"exit {exitCode}" });
    }

    /// <summary>A scripted harness that also projects a model credential — so ReattachAsync can RE-RESOLVE the credential (via this projector's provider) purely to rebuild the redactor for the resumed tail.</summary>
    private sealed class ProjectingHarness : IAgentHarness, IModelCredentialProjector
    {
        private readonly string _provider;
        private readonly string _envVar;

        public ProjectingHarness(string provider, string envVar)
        {
            _provider = provider;
            _envVar = envVar;
        }

        public string Kind => "scripted-projector";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "/bin/sh", Args = new[] { "-c", "true" }, Environment = task.Environment, TimeoutSeconds = task.TimeoutSeconds };

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine)
        {
            var line = rawLine.Trim();
            return line.Length == 0 ? Array.Empty<AgentEvent>() : new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = line, Data = JsonSerializer.SerializeToElement(new { line }) } };
        }

        public IAgentEventFolder CreateFolder() => new TestEventFolder((fold, exitCode) =>
            new() { Status = exitCode == 0 ? AgentRunStatus.Succeeded : AgentRunStatus.Failed, ExitReason = "completed", Summary = fold.LastText });

        public IReadOnlyList<string> SupportedProviders => new[] { _provider };

        public IReadOnlyDictionary<string, string> ProjectToEnv(ResolvedModelCredential credential) =>
            new Dictionary<string, string> { [_envVar] = credential.ApiKey ?? "" };
    }
}

using System.Diagnostics;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
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
                async (line, _) =>
                {
                    await svc.AppendEventAsync(runId, new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = line.Trim() }, CancellationToken.None);
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
            (await scope.Resolve<IAgentRunService>().ReclaimForReattachAsync(runId, CancellationToken.None)).ShouldBeTrue();

        await ReattachAsync(runId, new ScriptedHarness());

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            (await svc.GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded, "the re-attached observer tailed the live process to completion");

            (await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None)).Select(e => e.Text)
                .ShouldBe(new[] { "step1", "step2", "step3", "step4", "step5", "step6" }, "the timeline is continuous + each line appears EXACTLY ONCE (resume skipped the already-emitted prefix)");
        }
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
                (line, _) => { buffered.Add(new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = line.Trim() }); return Task.CompletedTask; },
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
            (await scope.Resolve<IAgentRunService>().ReclaimForReattachAsync(runId, CancellationToken.None)).ShouldBeTrue();

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
            (await scope.Resolve<IAgentRunService>().ReclaimForReattachAsync(runId, CancellationToken.None)).ShouldBeTrue();

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
            (await scope.Resolve<IAgentRunService>().ReclaimForReattachAsync(runId, CancellationToken.None)).ShouldBeTrue();

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
            (await scope.Resolve<IAgentRunService>().ReclaimForReattachAsync(runId, CancellationToken.None)).ShouldBeTrue();

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
            (await scope.Resolve<IAgentRunService>().ReclaimForReattachAsync(runId, CancellationToken.None)).ShouldBeTrue();

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
            (await scope.Resolve<IAgentRunService>().ReclaimForReattachAsync(runId, CancellationToken.None)).ShouldBeTrue();

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

            summary.ReattachedStaleRunning.ShouldBe(1, "the reconciler re-attached the stale-but-alive run");
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
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

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
                (await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None)).ReattachedStaleRunning
                    .ShouldBe(1, $"sweep {attempt} re-attaches (still within the attempt budget)");
            }

            // Budget exhausted → the next sweep abandons, so a permanently-unattachable-but-alive run still
            // reaches a terminal state instead of being reclaimed forever.
            await LapseLeaseAsync(runId);
            using (var scope = _fixture.BeginScope())
                (await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None)).MarkedAbandonedFromRunning
                    .ShouldBe(1, "past the re-attach budget the run is abandoned");

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

    private async Task ReattachAsync(Guid runId, IAgentHarness harness)
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
            scope.Resolve<IServiceScopeFactory>(),
            scope.Resolve<CodeSpaceDbContext>(),
            scope.Resolve<CodeSpace.Core.Services.Review.IStructuredCritic>(),
            NullLogger<AgentRunExecutor>.Instance);

        await executor.ReattachAsync(runId, CancellationToken.None);
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
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "scripted", Harness = "scripted", Model = "test-model", TimeoutSeconds = 1800 },
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
        db.Team.Add(new Team { Id = teamId, Slug = $"agent-{teamId:N}", Name = "Agent Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
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
    private sealed class ScriptedHarness : IAgentHarness
    {
        public string Kind => "scripted";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "/bin/sh", Args = new[] { "-c", "true" }, TimeoutSeconds = task.TimeoutSeconds };

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
            string.IsNullOrWhiteSpace(rawLine) ? Array.Empty<AgentEvent>() : new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine.Trim() } };

        public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode) =>
            exitCode == 0
                ? new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = events.Count > 0 ? events[^1].Text : null }
                : new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = $"exit {exitCode}" };
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

        public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode) =>
            new() { Status = exitCode == 0 ? AgentRunStatus.Succeeded : AgentRunStatus.Failed, ExitReason = "completed", Summary = events.Count > 0 ? events[^1].Text : null };

        public IReadOnlyList<string> SupportedProviders => new[] { _provider };

        public IReadOnlyDictionary<string, string> ProjectToEnv(ResolvedModelCredential credential) =>
            new Dictionary<string, string> { [_envVar] = credential.ApiKey ?? "" };
    }
}

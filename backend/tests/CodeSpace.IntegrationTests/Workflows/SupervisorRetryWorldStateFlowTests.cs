using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 HIGH fidelity (Rule 12): P0-1 retry world-state conservation — a supervisor RETRY of a subtask whose prior
/// attempt already pushed a branch must clone that branch, never a fresh checkout of the repository's default
/// branch. Driven through the REAL <see cref="RealSupervisorActionExecutor"/> against real Postgres, with a REAL
/// prior-attempt agent (real <see cref="AgentRunExecutor"/> + a real local bare git remote) so its
/// <see cref="PublishManifest"/> row carries a GENUINE branch, never a hand-faked one. Proves the forensic root
/// cause of run 96695645: a retry's clone ref is resolved from the SUBTASK'S OWN prior attempt, never silently
/// defaulted while the resume hint implies continuity that isn't actually there.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorRetryWorldStateFlowTests
{
    private const string NodeId = "sup";
    private const string Goal = "ship the retried feature";

    private readonly PostgresFixture _fixture;

    public SupervisorRetryWorldStateFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_retry_of_a_subtask_whose_prior_attempt_pushed_a_branch_clones_that_branch()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        var (priorAttemptRunId, _) = await RunPriorAttemptAsync(teamId, repoId, runId, "printf 'already done\\n' > done.txt; echo edited");
        var manifest = await SingleManifestAsync(priorAttemptRunId, teamId);
        manifest.Branch.ShouldNotBeNull("PublishMode=Branch + a bound credential → the prior attempt actually pushed");

        var context = ContextWith(runId, teamId, repoId,
            plan: Plan("sb"),
            priorAttempt: await FailedAttempt(teamId, "sb", priorAttemptRunId));

        var task = await ExecuteRetryAsync(context, "sb");

        task.Workspace.ShouldNotBeNull("retry world-state conservation pins an explicit clone ref");
        task.Workspace!.Repositories.Single().Ref.ShouldBe(manifest.Branch, "the retry clones the PRIOR ATTEMPT'S OWN branch, never the repository default");
        task.Goal.ShouldContain(manifest.Branch!, customMessage: "the server-authored continuity block names the prior attempt's branch in the agent's prompt");

        (await remote.FileOnBranchAsync(manifest.Branch!, "done.txt")).Trim().ShouldBe("already done", "the branch pinned really does contain the prior attempt's committed work");
    }

    [Fact]
    public async Task A_retry_does_not_resume_git_state_from_a_sole_concrete_manifest_for_another_repository()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        var credentialId = await SeedCredentialAsync(teamId);
        using var priorRemote = new BareRemote();
        using var retryRemote = new BareRemote();
        await priorRemote.SeedWithOneCommitAsync();
        await retryRemote.SeedWithOneCommitAsync();
        var priorRepositoryId = await SeedRepositoryAsync(teamId, priorRemote.Url, credentialId, RepositoryPublishMode.Branch);
        var retryRepositoryId = await SeedRepositoryAsync(teamId, retryRemote.Url, credentialId, RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        var (priorAttemptRunId, _) = await RunPriorAttemptAsync(teamId, priorRepositoryId, runId, "printf 'wrong repository\n' > done.txt; echo edited");
        var manifest = await SingleManifestAsync(priorAttemptRunId, teamId);
        manifest.RepositoryId.ShouldBe(priorRepositoryId);
        manifest.Branch.ShouldNotBeNull();

        var context = ContextWith(runId, teamId, retryRepositoryId,
            plan: Plan("sb"),
            priorAttempt: await FailedAttempt(teamId, "sb", priorAttemptRunId));

        var task = await ExecuteRetryAsync(context, "sb");

        task.Workspace.ShouldBeNull("repository A's sole concrete manifest cannot provide repository B's retry continuity");
        task.Goal.ShouldNotContain(manifest.Branch!, customMessage: "the retry prompt must not claim another repository's branch was preserved");
    }

    [Fact]
    public async Task A_retry_of_a_patch_only_prior_attempt_stays_on_the_default_branch_with_an_honest_redo_hint()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        // PatchOnly blocks the push — the prior attempt's work lives ONLY in its recorded patch, never a branch.
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.PatchOnly);
        var runId = await SeedSupervisorRunAsync(teamId);

        var (priorAttemptRunId, _) = await RunPriorAttemptAsync(teamId, repoId, runId, "printf 'patched only\\n' > patched.txt; echo edited");
        var manifest = await SingleManifestAsync(priorAttemptRunId, teamId);
        manifest.Branch.ShouldBeNull("the repo policy blocked the push — no branch to continue from");

        // Stamp a resumable session onto the prior attempt (the scripted harness itself carries no session id) so
        // the honest-redo hint's OTHER half — "your conversation IS restored" — is genuinely exercised too.
        await StampResumableSessionAsync(priorAttemptRunId, "sess-sb-patch-only", "the patch-only attempt's conversation\n");

        var context = ContextWith(runId, teamId, repoId,
            plan: Plan("sb"),
            priorAttempt: await FailedAttempt(teamId, "sb", priorAttemptRunId));

        var task = await ExecuteRetryAsync(context, "sb");

        task.Workspace.ShouldBeNull("no pushed branch to pin — the retry keeps the byte-identical default-branch clone");
        task.ResumeFromSessionId.ShouldBe("sess-sb-patch-only", "the conversation is still resumed");
        task.Goal.ShouldContain(AgentRetryContinuity.HonestNoContinuityHint, customMessage: "the goal must HONESTLY say the git changes were NOT preserved, since the conversation restore alone could otherwise imply the work is already on disk");
    }

    [Fact]
    public async Task A_retry_with_no_prior_attempt_at_all_is_a_byte_identical_cold_start()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        // No prior spawn/retry decision at all names "sb" — a genuine cold-start retry (e.g. the model retries a
        // subtask that was planned but never actually staged).
        var context = ContextWith(runId, teamId, repoId, plan: Plan("sb"), priorAttempt: null);

        var task = await ExecuteRetryAsync(context, "sb");

        task.Workspace.ShouldBeNull("no prior attempt exists — the default-branch clone stands, exactly as before P0-1");
    }

    [Fact]
    public async Task With_two_recorded_attempts_the_git_ref_always_matches_the_conversation_being_resumed()
    {
        // The literal-latest attempt (by decision order) and the RESUMABLE attempt can be two DIFFERENT prior runs —
        // e.g. the newest attempt crashed with no session while an older one both pushed a branch AND is resumable.
        // The git-staging lookup must key off the SAME attempt whose conversation is restored, never the independently-
        // resolved "latest" one, or the honest-redo hint would assert a falsehood while discarding a real branch.
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        var (olderAttemptRunId, _) = await RunPriorAttemptAsync(teamId, repoId, runId, "printf 'older attempt work\\n' > older.txt; echo edited");
        var olderManifest = await SingleManifestAsync(olderAttemptRunId, teamId);
        olderManifest.Branch.ShouldNotBeNull("the older attempt genuinely pushed");
        await StampResumableSessionAsync(olderAttemptRunId, "sess-older", "the older attempt's conversation\n");

        // The NEWER attempt's script exits non-zero (no commit, no manifest branch) and is never stamped with a
        // session — a realistic "crashed before publishing, no conversation captured" shape.
        var (newerAttemptRunId, _) = await RunFailingPriorAttemptAsync(teamId, repoId, runId, "exit 1");

        var context = new SupervisorTurnContext
        {
            Goal = Goal, SupervisorRunId = runId, TeamId = teamId, NodeId = NodeId, TurnNumber = 3,
            PriorDecisions = new[] { Plan("sb"), await FailedAttempt(teamId, "sb", olderAttemptRunId), await RetriedAttempt(teamId, "sb", newerAttemptRunId) },
            AgentProfile = new CodeSpace.Messages.Dtos.Agents.SupervisorAgentProfile { RepositoryId = repoId },
        };

        var task = await ExecuteRetryAsync(context, "sb");

        task.ResumeFromSessionId.ShouldBe("sess-older", "the only resumable attempt is the older one — the newer crashed with no session");
        task.Workspace.ShouldNotBeNull("the older (resumable) attempt's own branch must be pinned");
        task.Workspace!.Repositories.Single().Ref.ShouldBe(olderManifest.Branch, "the git ref belongs to the SAME attempt whose conversation is being resumed, never the literal-latest attempt's (which has no branch at all)");
        task.Goal.ShouldNotContain(AgentRetryContinuity.HonestNoContinuityHint, customMessage: "the resumed attempt's own branch IS preserved — asserting otherwise would be a lie");
    }

    // ── 3c: a unit whose HOST died resumes from its mid-run checkpoint ─────────────

    [Fact]
    public async Task A_retry_of_a_unit_whose_host_died_resumes_from_its_checkpoint()
    {
        // The population #1994 excluded. A supervisor unit whose host is lost is abandoned by the reconciler with NO
        // result at all, so the captured-transcript lookup every warm retry uses finds nothing. What survives is the
        // checkpoint on the row, and a terminal row still naming one is the signature of that abandon: every clean
        // landing — completion or cancel — releases those columns in its own terminal write.
        // MUTATION: drop the TryResumableFromCheckpoint fall-through → the retry cold-starts with no provenance → red.
        // MUTATION: drop the CheckpointAt branch in ApplyResumeRecord → the retry still resumes the checkpoint, but
        // unmarked (an unreadable ref would then fail the attempt), with no provenance and the ordinary honest-redo
        // line in place of the lost-host block → red.
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        var checkpointArtifactId = Guid.NewGuid();
        var lostAttemptRunId = await SeedHostLostAttemptAsync(teamId, repoId, runId, (checkpointArtifactId, "sess-lost-host"));

        var abandoned = await ReconcileUntilTerminalAsync(lostAttemptRunId);

        abandoned.Status.ShouldBe(AgentRunStatus.Failed, "the reconciler terminalizes a run whose host never came back");
        abandoned.SessionTranscriptCheckpointArtifactId.ShouldBe(checkpointArtifactId, "and the abandon KEEPS the checkpoint — releasing it is a clean landing's job, not the abandon's");

        var context = ContextWith(runId, teamId, repoId, plan: Plan("sb"), priorAttempt: await FailedAttempt(teamId, "sb", lostAttemptRunId));

        var task = await ExecuteRetryAsync(context, "sb");

        task.ResumeFromSessionId.ShouldBe("sess-lost-host", "the CLI is told WHICH conversation to resume — a transcript with no id names nothing");
        task.RestoredTranscriptArtifactId.ShouldBe(checkpointArtifactId, "the checkpoint rides as a REF the executor resolves just before invocation");
        task.RestoredTranscriptIsCheckpoint.ShouldBeTrue("best-effort bytes: unreadable must cost the conversation, never this retry attempt");
        task.ResumedFromCheckpointAt.ShouldNotBeNull("the launch stamps this onto the run's permanent confinement record");
        task.ResumedFromAgentRunId.ShouldBe(lostAttemptRunId, "which attempt took over from which is a column, not prose");
        task.Goal.ShouldContain(AgentRetryContinuity.LostHostPreamble, Case.Sensitive, "a restored conversation describes a machine that is gone, and the agent must be told");

        using var verify = _fixture.BeginScope();
        var respawn = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking()
            .Where(r => r.WorkflowRunId == runId && r.Id != lostAttemptRunId).OrderByDescending(r => r.CreatedDate).FirstAsync();

        respawn.ResumedFromAgentRunId.ShouldBe(lostAttemptRunId, "and the task's provenance is promoted onto the row, like AgentDefinitionId");
    }

    [Fact]
    public async Task A_retry_of_a_unit_lost_without_a_checkpoint_is_cold_and_claims_nothing()
    {
        // The same host loss from a unit that checkpointed nothing — it never opted in, or died before its first
        // checkpoint. Production leaves such a row with no session id either: the checkpoint stamp is the only writer
        // of a Running row's session id, and it writes both together. Nothing to restore, so the respawn cold-starts
        // and claims neither a lost machine nor a restored conversation.
        // A NEGATIVE CONTROL, shielded twice: the lookup never loads a row with no session id, and the guard would
        // refuse it anyway. No single-line mutation reaches it — dropping the guard alone stays green here.
        // MUTATION: drop the lookup's session-id filter AND TryResumableFromCheckpoint's guard → the retry claims a
        // conversation that was never written → red. The guard on its own is pinned where production can reach it:
        // the deliberate-cancel arm (its row keeps a session id) and the still-running arm.
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        var lostAttemptRunId = await SeedHostLostAttemptAsync(teamId, repoId, runId, checkpoint: null);

        (await ReconcileUntilTerminalAsync(lostAttemptRunId)).Status.ShouldBe(AgentRunStatus.Failed);

        var context = ContextWith(runId, teamId, repoId, plan: Plan("sb"), priorAttempt: await FailedAttempt(teamId, "sb", lostAttemptRunId));

        ShouldBeColdAndClaimNothing(await ExecuteRetryAsync(context, "sb"), "no checkpoint was taken, so there is no conversation to restore");
    }

    [Fact]
    public async Task A_retry_of_a_unit_that_was_deliberately_cancelled_is_cold_and_claims_no_host_loss()
    {
        // A deliberate cancel (an operator's, or the parent-terminal sweep's) is a CLEAN landing: nobody owes the
        // unit a continuation, so it releases its checkpoint exactly as completion does. Left in place, the next
        // retry of the same subtask — after an operator's Continue revives the run — would read the cancel as a host
        // loss and hand the agent three false claims (a lost machine, a checkpoint provenance stamp, and
        // resumed_from_agent_run_id), and the reference would pin the artifact Referenced for good.
        // MUTATION: drop the two checkpoint SetProperty calls from CancelRunningAsync → the cancelled row still names
        // its checkpoint → red.
        // MUTATION: relax TryResumableFromCheckpoint's guard to accept a bare session id (the cancel keeps it) → the
        // retry resumes a conversation with no transcript behind it → red, whichever path the resume then takes.
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        var cancelledRunId = await SeedLiveAttemptAsync(teamId, repoId, runId, (Guid.NewGuid(), "sess-cancelled"));

        (await CancelAsync(cancelledRunId)).ShouldBeTrue("the attempt was Running at the epoch the cancel read");

        using (var mid = _fixture.BeginScope())
        {
            var cancelled = await mid.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == cancelledRunId);
            cancelled.Status.ShouldBe(AgentRunStatus.Cancelled);
            cancelled.SessionTranscriptCheckpointArtifactId.ShouldBeNull("a clean landing releases the checkpoint");
            cancelled.SessionTranscriptCheckpointAt.ShouldBeNull();
            cancelled.SessionId.ShouldBe("sess-cancelled", "the cancel releases the checkpoint, not the session id — which is what makes the guard reachable here");
        }

        (await FindResumableAsync(teamId, runId)).ShouldBeNull("a cancelled attempt left nothing a retry may resume");

        var context = ContextWith(runId, teamId, repoId, plan: Plan("sb"), priorAttempt: await FailedAttempt(teamId, "sb", cancelledRunId));

        ShouldBeColdAndClaimNothing(await ExecuteRetryAsync(context, "sb"), "a deliberately cancelled attempt's machine was not lost, and it left no conversation to restore");
    }

    [Fact]
    public async Task A_retry_never_resumes_a_prior_attempt_that_is_still_running()
    {
        // The checkpoint columns are written while an attempt is Running, so a row still Running holds the checkpoint
        // of a session that may be live: a kill-wave is best-effort, and once a revived parent is Pending again the
        // orphan sweep stops selecting it. Resuming it would fork a conversation that is still being written.
        // MUTATION: drop the terminal-status guard from TryResumableFromCheckpoint → the retry resumes the live
        // session → red.
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        var liveRunId = await SeedLiveAttemptAsync(teamId, repoId, runId, (Guid.NewGuid(), "sess-still-live"));

        (await FindResumableAsync(teamId, runId)).ShouldBeNull("a Running attempt's checkpoint belongs to a session that may still be live");

        var context = ContextWith(runId, teamId, repoId, plan: Plan("sb"), priorAttempt: await FailedAttempt(teamId, "sb", liveRunId));

        ShouldBeColdAndClaimNothing(await ExecuteRetryAsync(context, "sb"), "nothing may resume a conversation another process is still writing");
    }

    // ── 3c: the tree sentence describes the resumed attempt's OWN git state ─────────

    [Fact]
    public async Task A_dependent_unit_lost_before_it_pushed_is_told_its_work_is_gone_not_that_its_producers_branch_is_its_own()
    {
        // A dependent unit is cloned at its producer's handoff branch. When its own attempt lost its host before it
        // pushed, that branch holds the PRODUCER's work and none of this attempt's, so the tree sentence must be the
        // honest "nothing was preserved" — never "your previous attempt published `<the producer's branch>`".
        // MUTATION: pass the effective clone ref (effectiveStaging.Ref) as the resumed attempt's workspaceRef → the
        // goal names the producer's branch as this attempt's published work → red.
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        var producer = await RunProducerAsync(teamId, repoId, runId);
        var lostAttemptRunId = await SeedHostLostAttemptAsync(teamId, repoId, runId, (Guid.NewGuid(), "sess-dependent"));

        (await ReconcileUntilTerminalAsync(lostAttemptRunId)).Status.ShouldBe(AgentRunStatus.Failed);

        var task = await ExecuteRetryAsync(DependentContext(runId, teamId, repoId, producer.Spawn, await FailedAttempt(teamId, "sb", lostAttemptRunId, sequence: 3)), "sb");

        task.Workspace!.Repositories.Single().Ref.ShouldBe(producer.Branch, "staging is unchanged: a dependent unit still clones its producer's handoff");
        task.RestoredTranscriptIsCheckpoint.ShouldBeTrue();
        task.Goal.ShouldContain(AgentRetryContinuity.LostHostPreamble, Case.Sensitive);
        task.Goal.ShouldContain(AgentRetryContinuity.HonestNoContinuityHint, Case.Sensitive, "the lost attempt pushed nothing, so none of its tree survived");
        task.Goal.ShouldNotContain(AgentRetryContinuity.LostHostPublishedBranchHint(producer.Branch), Case.Sensitive, "the producer's branch is not this attempt's published work");
    }

    [Fact]
    public async Task A_dependent_unit_resumed_from_a_captured_transcript_is_told_its_own_changes_were_not_preserved()
    {
        // The same rule on the ordinary path. An attempt that finished without pushing left its changes in a
        // workspace nobody will see again; the clone ref is still its producer's handoff, but that is not THIS
        // attempt's work, so it must not suppress the honest-redo line — which the effective ref used to do.
        // MUTATION: pass effectiveStaging.Ref as workspaceRef → the producer's branch suppresses the line → red.
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        var producer = await RunProducerAsync(teamId, repoId, runId);
        var (failedAttemptRunId, _) = await RunFailingPriorAttemptAsync(teamId, repoId, runId, "exit 1");
        (await ManifestsAsync(failedAttemptRunId, teamId)).ShouldBeEmpty("precondition: the failed attempt pushed nothing of its own");

        await StampResumableSessionAsync(failedAttemptRunId, "sess-captured", "the dependent attempt's conversation\n");

        var task = await ExecuteRetryAsync(DependentContext(runId, teamId, repoId, producer.Spawn, await FailedAttempt(teamId, "sb", failedAttemptRunId, sequence: 3)), "sb");

        task.ResumeFromSessionId.ShouldBe("sess-captured", "the captured conversation is still resumed");
        task.Workspace!.Repositories.Single().Ref.ShouldBe(producer.Branch, "staging is unchanged: a dependent unit still clones its producer's handoff");
        task.Goal.ShouldContain(AgentRetryContinuity.HonestNoContinuityHint, Case.Sensitive, "the restored conversation describes changes this workspace does not contain");
        task.Goal.ShouldNotContain(AgentRetryContinuity.LostHostPreamble, Case.Sensitive, "an attempt that finished did not lose its machine");
    }

    [Fact]
    public async Task A_dependent_unit_that_pushed_its_own_branch_before_its_host_died_is_told_that_branch_is_here()
    {
        // The other side of the same rule: when the lost attempt DID push, its own branch outranks the producer's
        // handoff as the clone ref, the published work is there, and the sentence names THAT branch.
        // MUTATION: drop the resumed attempt's ref (pass workspaceRef: null) → the goal says nothing was preserved
        // while the clone holds this attempt's own pushed work → red.
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        var producer = await RunProducerAsync(teamId, repoId, runId);
        var (lostAttemptRunId, _) = await RunPriorAttemptAsync(teamId, repoId, runId, "printf 'own work\\n' > own.txt; echo edited");
        var ownBranch = (await SingleManifestAsync(lostAttemptRunId, teamId)).Branch.ShouldNotBeNull("precondition: the attempt published its own branch before its host died");

        await LoseTheHostAfterPublishingAsync(lostAttemptRunId, (Guid.NewGuid(), "sess-published"));

        var task = await ExecuteRetryAsync(DependentContext(runId, teamId, repoId, producer.Spawn, await FailedAttempt(teamId, "sb", lostAttemptRunId, sequence: 3)), "sb");

        task.Workspace!.Repositories.Single().Ref.ShouldBe(ownBranch, "the attempt's own pushed branch outranks its producer's handoff");
        task.RestoredTranscriptIsCheckpoint.ShouldBeTrue();
        task.Goal.ShouldContain(AgentRetryContinuity.LostHostPublishedBranchHint(ownBranch), Case.Sensitive, "the published work IS here, and the agent is told which branch holds it");
        task.Goal.ShouldNotContain(AgentRetryContinuity.LostHostPublishedBranchHint(producer.Branch), Case.Sensitive);
        task.Goal.ShouldNotContain(AgentRetryContinuity.HonestNoContinuityHint, Case.Sensitive, "asserting its work is gone would be a lie");
    }

    // ── 3c: the checkpoint opt-in at the staging seam ─────────────────────────────

    [Theory]
    [InlineData(2, 8, true)]    // room for this retry AND another after it
    [InlineData(7, 8, false)]   // this retry lands ON the cap — nothing can follow, so nobody would read a checkpoint
    public async Task A_staged_unit_checkpoints_only_while_the_run_could_still_respawn_it(int totalSpawned, int cap, bool checkpoints)
    {
        // The opt-in, pinned at the STAGING SEAM rather than only as a pure predicate: this is the one place every
        // spawn wave, retry and resolve passes through, so it is where the envelope either carries the flag or not.
        // MUTATION: set CheckpointSessionTranscript unconditionally (or never) at that seam → one arm reds.
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        var context = ContextWith(runId, teamId, repoId, plan: Plan("sb"), priorAttempt: null) with { TotalSpawnedAgents = totalSpawned, MaxTotalSpawns = cap };

        (await ExecuteRetryAsync(context, "sb")).CheckpointSessionTranscript.ShouldBe(checkpoints);
    }

    [Theory]
    [InlineData(5, 8, true)]    // 5 + 2 = 7 < 8: a retry still fits after the whole wave
    [InlineData(6, 8, false)]   // 6 + 2 = 8: the WAVE lands on the cap, though one agent alone (6 + 1 = 7) would not
    public async Task A_spawn_wave_checkpoints_only_when_a_retry_still_fits_after_the_whole_wave(int totalSpawned, int cap, bool checkpoints)
    {
        // The seam reads the WAVE's size, not one agent's: by the time any retry is decided, every agent this wave
        // stages is already on the tape and counted.
        // MUTATION: pass 1 instead of tasks.Count at the staging seam → the (6, 8) arm checkpoints both units → red.
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        var context = ContextWith(runId, teamId, repoId, plan: Plan(("sa", null), ("sb", null)), priorAttempt: null) with { TotalSpawnedAgents = totalSpawned, MaxTotalSpawns = cap };

        var staged = await ExecuteSpawnAsync(context, "sa", "sb");

        staged.Count.ShouldBe(2, "one wave, two units");
        staged.ShouldAllBe(t => t.CheckpointSessionTranscript == checkpoints);
    }

    private static void ShouldBeColdAndClaimNothing(AgentTask task, string because)
    {
        task.ResumeFromSessionId.ShouldBeNull(because);
        task.RestoredTranscript.ShouldBeNull(because);
        task.RestoredTranscriptArtifactId.ShouldBeNull(because);
        task.RestoredTranscriptIsCheckpoint.ShouldBeFalse(because);
        task.ResumedFromCheckpointAt.ShouldBeNull(because);
        task.ResumedFromAgentRunId.ShouldBeNull(because);
        task.Goal.ShouldNotContain(AgentRetryContinuity.LostHostPreamble, customMessage: because);
        task.Goal.ShouldNotContain(AgentRetryContinuity.HonestNoContinuityHint, customMessage: because);
    }

    /// <summary>
    /// A prior attempt of "sb" in the state a HOST LOSS leaves it: created through the REAL
    /// <see cref="IAgentRunService"/> (so its envelope, SubtaskId and columns are the production shape), claimed by a
    /// worker that is now gone — its owner id and fence epoch still on the row, its lease lapsed — with a durable
    /// handle minted on a host that never came back whose own wall clock has passed: the exact shape
    /// <c>AgentRunReconcilerService.DeferToTheMintingHostAsync</c> stops deferring and abandons. A checkpoint, when
    /// given, is stamped the way the observer's drain tick leaves it — artifact, time and session id together, the
    /// only way production writes a Running row's session id.
    ///
    /// <para>Left un-executed rather than run and then aged, and the reason bounds what these arms prove: the stale
    /// sweep deliberately EXCLUDES a run carrying events inside <see cref="AgentRunLiveness.Window"/> (a streaming
    /// agent whose lease merely lapsed must never be abandoned), and <c>agent_run_event</c> is append-only by
    /// trigger — so a run that genuinely executed cannot be backdated into this shape. Such a run also published
    /// nothing; the published-branch case is <see cref="LoseTheHostAfterPublishingAsync"/>'s.</para>
    /// </summary>
    private Task<Guid> SeedHostLostAttemptAsync(Guid teamId, Guid repositoryId, Guid supervisorRunId, (Guid ArtifactId, string SessionId)? checkpoint) =>
        SeedClaimedAttemptAsync(teamId, repositoryId, supervisorRunId, checkpoint, hostLost: true);

    /// <summary>A prior attempt of "sb" still RUNNING on a live worker — claimed, its lease fresh, a checkpoint on the row. What a deliberate cancel lands on, and what a best-effort kill-wave or a revived parent can leave behind while a retry is decided.</summary>
    private Task<Guid> SeedLiveAttemptAsync(Guid teamId, Guid repositoryId, Guid supervisorRunId, (Guid ArtifactId, string SessionId) checkpoint) =>
        SeedClaimedAttemptAsync(teamId, repositoryId, supervisorRunId, checkpoint, hostLost: false);

    private async Task<Guid> SeedClaimedAttemptAsync(Guid teamId, Guid repositoryId, Guid supervisorRunId, (Guid ArtifactId, string SessionId)? checkpoint, bool hostLost)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        // The envelope a supervisor unit really carries: SubtaskId is what FindResumableSubtaskAttemptAsync matches
        // on, and CheckpointSessionTranscript is the opt-in that let its executor checkpoint at all.
        var created = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "do sb", Harness = "scripted", Model = "test-model", RepositoryId = repositoryId, SubtaskId = "sb", CheckpointSessionTranscript = true },
            teamId, supervisorRunId, NodeId, iterationKey: "", cancellationToken: CancellationToken.None);

        var run = await db.AgentRun.SingleAsync(r => r.Id == created.Id);
        var claimedAt = DateTimeOffset.UtcNow - (hostLost ? TimeSpan.FromMinutes(20) : TimeSpan.FromMinutes(2));

        run.Status = AgentRunStatus.Running;
        run.OwnerId = Guid.NewGuid();
        run.FenceEpoch = 1;
        run.StartedAt = claimedAt;
        run.HeartbeatAt = claimedAt;
        run.LeaseExpiresAt = hostLost ? claimedAt + AgentRunLiveness.Window : DateTimeOffset.UtcNow + AgentRunLiveness.Window;
        run.RunnerHandleJson = hostLost ? JsonSerializer.Serialize(ForeignHostHandle(), AgentJson.Options) : null;
        run.SessionId = checkpoint?.SessionId;
        run.SessionTranscriptCheckpointArtifactId = checkpoint?.ArtifactId;
        run.SessionTranscriptCheckpointAt = checkpoint is null ? null : claimedAt + TimeSpan.FromMinutes(1);
        await db.SaveChangesAsync();

        return run.Id;
    }

    /// <summary>A durable handle minted on a host that never came back, whose own wall clock has already passed — the reconciler cannot probe it from here and stops deferring to it.</summary>
    private static SandboxHandle ForeignHostHandle()
    {
        var spoolDirectory = Path.Combine(Path.GetTempPath(), "cs-supervisor-host-loss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(spoolDirectory);

        return new SandboxHandle { Kind = "local", ProcessId = 0x7FFFFFFF, LaunchHost = "a-host-that-never-came-back", SpoolDirectory = spoolDirectory, Deadline = DateTimeOffset.UtcNow.AddMinutes(-1) };
    }

    /// <summary>
    /// Turn an attempt that really ran and PUBLISHED into the row a host loss leaves when the machine dies between
    /// the executor's publish and its terminal write: the manifest and the pushed branch are already real, and the
    /// abandon then writes Failed with no result while keeping the checkpoint. Written directly rather than through
    /// the reconciler because a run that really executed carries fresh events, which the stale sweep skips, and
    /// <c>agent_run_event</c> is append-only — the reconciler's own abandon is what
    /// <see cref="A_retry_of_a_unit_whose_host_died_resumes_from_its_checkpoint"/> drives.
    /// </summary>
    private async Task LoseTheHostAfterPublishingAsync(Guid agentRunId, (Guid ArtifactId, string SessionId) checkpoint)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await db.AgentRun.SingleAsync(r => r.Id == agentRunId);

        run.Status = AgentRunStatus.Failed;
        run.ResultJson = null;
        run.SessionId = checkpoint.SessionId;
        run.SessionTranscriptCheckpointArtifactId = checkpoint.ArtifactId;
        run.SessionTranscriptCheckpointAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Sweep with the REAL reconciler until the row this test owns is terminal, and return it. The stale sweep is
    /// deployment-wide and takes the oldest lapsed leases first, <see cref="AgentRunReconcilerService.BatchSize"/> at
    /// a time, so rows other tests left behind can fill a sweep; each sweep terminalizes what it takes, so a bounded
    /// number of them reaches ours.
    /// </summary>
    private async Task<AgentRun> ReconcileUntilTerminalAsync(Guid agentRunId)
    {
        const int maxSweeps = 10;

        for (var sweep = 0; sweep < maxSweeps; sweep++)
        {
            using var scope = _fixture.BeginScope();
            await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

            var row = await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == agentRunId);
            if (AgentRunStateMachine.IsTerminal(row.Status)) return row;
        }

        throw new ShouldAssertException($"agent run {agentRunId} was still not terminal after {maxSweeps} reconciler sweeps. Check that its lease has lapsed, that it has no event inside AgentRunLiveness.Window, and that its handle's deadline has passed — the reconciler logs 'leaving agent run ... alone' when it defers.");
    }

    private async Task<bool> CancelAsync(Guid agentRunId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IAgentRunService>().CancelRunningAsync(agentRunId, "Cancelled by an operator.", AgentRunAbandonCause.OperatorCancelled, CancellationToken.None);
    }

    private async Task<ResumableSession?> FindResumableAsync(Guid teamId, Guid supervisorRunId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IAgentRunService>().FindResumableSubtaskAttemptAsync(teamId, supervisorRunId, "sb", CancellationToken.None);
    }

    /// <summary>Run the plan's producer "pa" for real and record it as a Succeeded spawn — the handoff a dependent "sb" is cloned at. Returns the producer's pushed branch and that spawn decision.</summary>
    private async Task<(string Branch, SupervisorPriorDecision Spawn)> RunProducerAsync(Guid teamId, Guid repositoryId, Guid supervisorRunId)
    {
        var (producerRunId, _) = await RunPriorAttemptAsync(teamId, repositoryId, supervisorRunId, "printf 'producer work\\n' > producer.txt; echo edited", subtaskId: "pa");
        var branch = (await SingleManifestAsync(producerRunId, teamId)).Branch.ShouldNotBeNull("precondition: the producer really pushed, so a dependent is cloned at its branch");

        return (branch, await SucceededSpawn(teamId, "pa", producerRunId));
    }

    // ─── Drive the real executor ──────────────────────────────────────────────────

    private async Task<AgentTask> ExecuteRetryAsync(SupervisorTurnContext context, string subtaskId)
    {
        using var scope = _fixture.BeginScope();
        var executor = scope.Resolve<ISupervisorActionExecutor>();

        var payload = JsonSerializer.Serialize(new SupervisorRetryPayload { SubtaskId = subtaskId }, AgentJson.Options);
        var decision = new SupervisorDecision { Kind = SupervisorDecisionKinds.Retry, PayloadJson = payload };

        await executor.ExecuteAsync(decision, context, CancellationToken.None);

        var run = await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking()
            .Where(r => r.WorkflowRunId == context.SupervisorRunId && r.NodeId == NodeId)
            .OrderByDescending(r => r.CreatedDate).FirstAsync();

        return JsonSerializer.Deserialize<AgentTask>(run.TaskJson, AgentJson.Options)!;
    }

    /// <summary>One spawn wave through the real executor, returning the envelope of every unit it staged.</summary>
    private async Task<IReadOnlyList<AgentTask>> ExecuteSpawnAsync(SupervisorTurnContext context, params string[] subtaskIds)
    {
        using var scope = _fixture.BeginScope();

        var payload = JsonSerializer.Serialize(new SupervisorSpawnPayload { SubtaskIds = subtaskIds }, AgentJson.Options);

        await scope.Resolve<ISupervisorActionExecutor>().ExecuteAsync(new SupervisorDecision { Kind = SupervisorDecisionKinds.Spawn, PayloadJson = payload }, context, CancellationToken.None);

        var runs = await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == context.SupervisorRunId && r.NodeId == NodeId).ToListAsync();

        return runs.Select(r => JsonSerializer.Deserialize<AgentTask>(r.TaskJson, AgentJson.Options)!).ToList();
    }

    // ─── Context / decision-tape builders ─────────────────────────────────────────

    private static SupervisorTurnContext ContextWith(Guid runId, Guid teamId, Guid repositoryId, SupervisorPriorDecision plan, SupervisorPriorDecision? priorAttempt) => new()
    {
        Goal = Goal,
        SupervisorRunId = runId,
        TeamId = teamId,
        NodeId = NodeId,
        TurnNumber = 3,
        PriorDecisions = priorAttempt is null ? new[] { plan } : new[] { plan, priorAttempt },
        AgentProfile = new CodeSpace.Messages.Dtos.Agents.SupervisorAgentProfile { RepositoryId = repositoryId },
    };

    /// <summary>The dependent shape: "sb" depends on the producer "pa", whose spawn and "sb"'s own recorded attempt follow the plan on the tape.</summary>
    private static SupervisorTurnContext DependentContext(Guid runId, Guid teamId, Guid repositoryId, SupervisorPriorDecision producerSpawn, SupervisorPriorDecision dependentAttempt)
    {
        var plan = Plan(("pa", null), ("sb", new[] { "pa" }));

        return ContextWith(runId, teamId, repositoryId, plan, priorAttempt: null) with { PriorDecisions = new[] { plan, producerSpawn, dependentAttempt } };
    }

    private static SupervisorPriorDecision Plan(string subtaskId) => Plan((subtaskId, null));

    private static SupervisorPriorDecision Plan(params (string Id, string[]? DependsOn)[] subtasks)
    {
        var payload = JsonSerializer.Serialize(new SupervisorPlanPayload
        {
            Goal = Goal,
            Subtasks = subtasks.Select(s => new SupervisorPlannedSubtask { Id = s.Id, Title = s.Id, Instruction = $"do {s.Id}", DependsOn = s.DependsOn }).ToList(),
        }, AgentJson.Options);

        return new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payload, OutcomeJson = "{}" };
    }

    /// <summary>A prior FAILED spawn recording (subtaskId, REAL agentRunId) — the positional subtaskIds[i] ↔ agentResults[i] shape <see cref="SupervisorDependencyGate"/> reads to find "this subtask's latest attempt", UNFILTERED on success (the whole point of a retry).</summary>
    private Task<SupervisorPriorDecision> FailedAttempt(Guid teamId, string subtaskId, Guid agentRunId, long sequence = 2) =>
        RecordedSpawn(teamId, subtaskId, agentRunId, ("Failed", "acceptance failed"), sequence);

    /// <summary>A prior SUCCEEDED spawn of a producer — what dependency staging reads to hand its branch to a dependent.</summary>
    private Task<SupervisorPriorDecision> SucceededSpawn(Guid teamId, string subtaskId, Guid agentRunId) =>
        RecordedSpawn(teamId, subtaskId, agentRunId, ("Succeeded", null), sequence: 2);

    private async Task<SupervisorPriorDecision> RecordedSpawn(Guid teamId, string subtaskId, Guid agentRunId, (string Status, string? Error) outcome, long sequence)
    {
        var manifests = await ManifestsAsync(agentRunId, teamId);

        var result = new SupervisorAgentResult { AgentRunId = agentRunId, Status = outcome.Status, Error = outcome.Error, ProducedBranch = manifests.FirstOrDefault()?.Branch };

        var payload = JsonSerializer.Serialize(new SupervisorSpawnPayload { SubtaskIds = new[] { subtaskId } }, AgentJson.Options);
        var outcomeJson = JsonSerializer.Serialize(new { agentRunIds = new[] { agentRunId }, agentCount = 1, agentResults = new[] { result } }, AgentJson.Options);

        return new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = sequence, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payload, OutcomeJson = outcomeJson };
    }

    private async Task<IReadOnlyList<PublishManifest>> ManifestsAsync(Guid agentRunId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IPublishManifestStore>().ListForAgentRunAsync(agentRunId, teamId, CancellationToken.None);
    }

    /// <summary>A prior FAILED retry recording (subtaskId, REAL agentRunId), SEQUENCED AFTER <see cref="FailedAttempt"/> — the decision-tape-literal "latest attempt" <see cref="SupervisorDependencyGate.LatestAgentRunId"/> reads, independent of whether it is actually resumable.</summary>
    private async Task<SupervisorPriorDecision> RetriedAttempt(Guid teamId, string subtaskId, Guid agentRunId)
    {
        using var scope = _fixture.BeginScope();
        var manifests = await scope.Resolve<IPublishManifestStore>().ListForAgentRunAsync(agentRunId, teamId, CancellationToken.None);

        var result = new SupervisorAgentResult { AgentRunId = agentRunId, Status = "Failed", Error = "crashed before publishing", ProducedBranch = manifests.FirstOrDefault()?.Branch };

        var payload = JsonSerializer.Serialize(new SupervisorRetryPayload { SubtaskId = subtaskId }, AgentJson.Options);
        var outcome = JsonSerializer.Serialize(new { agentRunIds = new[] { agentRunId }, agentCount = 1, agentResults = new[] { result } }, AgentJson.Options);

        return new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 3, DecisionKind = SupervisorDecisionKinds.Retry, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payload, OutcomeJson = outcome };
    }

    // ─── Real prior-attempt execution (real AgentRunExecutor + real git) ──────────

    /// <summary>Run ONE real "prior attempt" agent (a scripted /bin/sh harness) through the REAL AgentRunExecutor against the real repo — a genuine PublishManifest row + (PublishMode-dependent) a genuine pushed branch results. Stamped with <see cref="AgentTask.SubtaskId"/> so <see cref="IAgentRunService.FindResumableSubtaskAttemptAsync"/> can find it by subtask id, exactly as a real supervisor-spawned agent would be. Mirrors <see cref="SupervisorDependencyStagingFlowTests.RunProducerAsync"/>.</summary>
    private async Task<(Guid AgentRunId, string ResultJson)> RunPriorAttemptAsync(Guid teamId, Guid repositoryId, Guid supervisorRunId, string script, string subtaskId = "sb")
    {
        var finished = await RunScriptedAttemptAsync(teamId, repositoryId, supervisorRunId, script, subtaskId);
        finished.Status.ShouldBe(AgentRunStatus.Succeeded, "the prior attempt must genuinely push for its manifest to carry a real branch");

        return (finished.Id, finished.ResultJson!);
    }

    /// <summary>The multi-attempt-divergence scenario's NEWER attempt: crashes (non-zero exit) before ever publishing — no manifest row, no session, exactly the "captured nothing" shape a real infra failure leaves.</summary>
    private async Task<(Guid AgentRunId, string ResultJson)> RunFailingPriorAttemptAsync(Guid teamId, Guid repositoryId, Guid supervisorRunId, string script, string subtaskId = "sb")
    {
        var finished = await RunScriptedAttemptAsync(teamId, repositoryId, supervisorRunId, script, subtaskId);
        finished.Status.ShouldBe(AgentRunStatus.Failed, "the scripted harness's non-zero exit is a genuine failure, never silently treated as success");

        return (finished.Id, finished.ResultJson!);
    }

    private async Task<AgentRun> RunScriptedAttemptAsync(Guid teamId, Guid repositoryId, Guid supervisorRunId, string script, string subtaskId)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "produce", Harness = "scripted", Model = "test-model", RepositoryId = repositoryId, SubtaskId = subtaskId },
            teamId, supervisorRunId, NodeId, iterationKey: "", cancellationToken: CancellationToken.None);

        var executor = new AgentRunExecutor(
            scope.Resolve<IAgentRunService>(),
            new AgentHarnessRegistry(new IAgentHarness[] { new ScriptedHarness(script) }),
            new HarnessModelReconciler(new AgentHarnessRegistry(new IAgentHarness[] { new ScriptedHarness(script) }), scope.Resolve<IModelPoolSelector>(), scope.Resolve<CodeSpaceDbContext>()),
            scope.Resolve<ISandboxRunnerRegistry>(),
            scope.Resolve<IAgentWorkspaceResolver>(),
            scope.Resolve<IModelCredentialResolver>(),
            scope.Resolve<IWorkspaceProviderRegistry>(),
            scope.Resolve<IAgentRunCompletionNotifier>(),
            scope.Resolve<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            scope.Resolve<CodeSpaceDbContext>(),
            scope.Resolve<CodeSpace.Core.Services.Review.IStructuredCritic>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactStore>(),
            scope.Resolve<IPublishManifestStore>(), scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IArtifactManifestStore>(), scope.Resolve<CodeSpace.Core.Services.Agents.Capture.ICaptureIntentService>(),
            scope.Resolve<IEnumerable<IPublishGuard>>(),
            NullLogger<AgentRunExecutor>.Instance);

        await executor.ExecuteAsync(run.Id, CancellationToken.None);

        return await scope.Resolve<IAgentRunService>().GetAsync(run.Id, CancellationToken.None);
    }

    private async Task<PublishManifest> SingleManifestAsync(Guid agentRunId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return (await scope.Resolve<IPublishManifestStore>().ListForAgentRunAsync(agentRunId, teamId, CancellationToken.None)).ShouldHaveSingleItem();
    }

    /// <summary>Stamp a resumable session onto an already-terminal agent run — the scripted harness itself carries no session id, so this mirrors what a real Claude-harness run would have persisted on completion (<c>AgentRun.SessionId</c> + the result's <c>SessionTranscript</c>).</summary>
    private async Task StampResumableSessionAsync(Guid agentRunId, string sessionId, string transcript)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var run = await db.AgentRun.SingleAsync(r => r.Id == agentRunId);
        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;

        run.SessionId = sessionId;
        run.ResultJson = JsonSerializer.Serialize(result with { SessionId = sessionId, SessionTranscript = transcript }, AgentJson.Options);

        await db.SaveChangesAsync();
    }

    // ─── Seeding (team / credential / repository / supervisor run) ────────────────

    private async Task<Guid> SeedTeamAsync() => (await WorkflowsTestSeed.SeedTeamAsync(_fixture)).TeamId;

    private async Task<Guid> SeedCredentialAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = await FindOrCreateProviderInstanceAsync(db, teamId);

        var serializer = scope.Resolve<ICredentialPayloadSerializer>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var payloadJson = serializer.Serialize(new PatPayload { Token = "retry-world-state-e2e-token" });

        var credentialId = Guid.NewGuid();
        db.Credential.Add(new Credential
        {
            Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId,
            AuthType = AuthType.Pat, DisplayName = "clone cred",
            EncryptedPayload = encryptor.Encrypt(payloadJson), Status = CredentialStatus.Active,
        });

        await db.SaveChangesAsync();
        return credentialId;
    }

    private async Task<Guid> FindOrCreateProviderInstanceAsync(CodeSpaceDbContext db, Guid teamId)
    {
        var existing = await db.ProviderInstance.Where(p => p.TeamId == teamId).Select(p => p.Id).FirstOrDefaultAsync();
        if (existing != Guid.Empty) return existing;

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = "https://local" });
        await db.SaveChangesAsync();
        return instanceId;
    }

    private async Task<Guid> SeedRepositoryAsync(Guid teamId, string cloneUrl, Guid credentialId, RepositoryPublishMode publishMode)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = await FindOrCreateProviderInstanceAsync(db, teamId);

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = "main", CloneUrlHttps = cloneUrl, WebUrl = "https://local/org/repo",
            PublishMode = publishMode,
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId)
    {
        using var scopeAsOperator = await WorkflowsTestSeed.BeginSeedOperatorScopeAsync(_fixture, teamId).ConfigureAwait(false);
        var workflowId = await scopeAsOperator.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-retry-world-state-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json($$"""{"goal":"{{Goal}}"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<EdgeDefinition> { new() { From = "start", To = NodeId }, new() { From = NodeId, To = "end" } },
            },
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });

        return await WorkflowsTestSeed.SeedAdmittedManualRunAsync(_fixture, workflowId, teamId).ConfigureAwait(false);
    }

    // ─── Git helpers ────────────────────────────────────────────────────────────

    private static async Task<bool> GitAvailableAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>A bare local repo standing in for the agents' remote, plus ref inspection via <c>git --git-dir</c> — real-git ground truth. Mirrors <see cref="SupervisorDependencyStagingFlowTests.BareRemote"/>.</summary>
    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-retry-world-state-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;

        public BareRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task SeedWithOneCommitAsync(string fileName = "README.md", string content = "base")
        {
            await RunGitAsync(_root, "init", "--bare", "-b", "main", _bare);

            var seed = Path.Combine(_root, "seed");
            Directory.CreateDirectory(seed);
            await RunGitAsync(seed, "clone", _bare, seed);
            await RunGitAsync(seed, "config", "user.email", "test@codespace.dev");
            await RunGitAsync(seed, "config", "user.name", "Test");
            await RunGitAsync(seed, "config", "commit.gpgsign", "false");
            await File.WriteAllTextAsync(Path.Combine(seed, fileName), content);
            await RunGitAsync(seed, "add", ".");
            await RunGitAsync(seed, "commit", "-m", "seed");
            await RunGitAsync(seed, "push", "origin", "main");
        }

        public Task<string> FileOnBranchAsync(string branch, string file) => RunGitAsync(_root, "--git-dir", _bare, "show", $"{branch}:{file}");

        private static async Task<string> RunGitAsync(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(
                new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);

            if (result.Status != SandboxStatus.Success)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");

            return result.Stdout;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>A CLI-less test harness: builds a /bin/sh invocation from a fixed script. Mirrors <see cref="SupervisorDependencyStagingFlowTests.ScriptedHarness"/>.</summary>
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

        public IAgentEventFolder CreateFolder() => new TestEventFolder((fold, exitCode) =>
            exitCode == 0
                ? new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = fold.LastText }
                : new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = $"exit {exitCode}" });
    }
}

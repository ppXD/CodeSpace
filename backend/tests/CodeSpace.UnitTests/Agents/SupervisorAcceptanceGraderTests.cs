using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: pins A2 — the supervisor's objective acceptance adapter (<see cref="SupervisorAcceptanceGrader"/>),
/// driven against fakes at the workspace + grader seams. Proves the adapter (1) clones the repo at the produced
/// BRANCH (threads it as the resolver ref), (2) builds the grading context from the clone dir + the authored argv +
/// timeout and resolves the TestsPass oracle, (3) returns the oracle's verdict verbatim, (4) ALWAYS removes the clone
/// — even when the grade throws — and (5) FAILS CLOSED (a Failed grade, never a silent pass) when the repo/branch
/// cannot be resolved or cloned, while letting a genuine runner-infra throw propagate. The clone + grade are real
/// production seams (registries) wired to fakes; the I/O fidelity is proven at the integration/E2E tiers.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorAcceptanceGraderTests
{
    private static readonly string[] Command = { "./check.sh", "--ci" };

    /// <summary>The spec-shaped acceptance the adapter now takes (triad S7) — same argv, optional kind.</summary>
    private static SupervisorAcceptanceSpec Spec(BenchmarkGradingKind? kind = null) => new() { Command = Command, Kind = kind };

    [Fact]
    public async Task Clones_the_repo_at_the_produced_branch()
    {
        var resolver = new FakeResolver(new WorkspaceRequest { RepositoryUrl = "file:///r" });
        var grader = Build(resolver, new FakeGrader(Pass));

        var repoId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        await grader.GradeAsync(repoId, teamId, "feat/x", Spec(), 30, CancellationToken.None);

        resolver.RepositoryId.ShouldBe(repoId);
        resolver.TeamId.ShouldBe(teamId);
        resolver.Ref.ShouldBe("feat/x", "the produced branch is cloned by being passed as the resolver ref");
    }

    [Fact]
    public async Task Builds_the_grading_context_from_the_clone_and_authored_command()
    {
        var oracle = new FakeGrader(Pass);
        var grader = Build(new FakeResolver(new WorkspaceRequest { RepositoryUrl = "file:///r" }), oracle, handleDir: "/tmp/clone-xyz");

        await grader.GradeAsync(Guid.NewGuid(), Guid.NewGuid(), "b", Spec(), 45, CancellationToken.None);

        oracle.ResolvedKind.ShouldBe(BenchmarkGradingKind.TestsPass, "the TestsPass oracle grades the result");
        oracle.Context.ShouldNotBeNull();
        oracle.Context!.WorkspaceDirectory.ShouldBe("/tmp/clone-xyz", "the grade runs in the cloned workspace");
        oracle.Context.Task.TestCommand.ShouldBe(Command, "the authored argv is the grading command");
        oracle.Context.Task.TimeoutSeconds.ShouldBe(45);
        oracle.Context.Task.Grading.ShouldBe(BenchmarkGradingKind.TestsPass);
    }

    [Theory]
    [InlineData(BenchmarkGradingKind.TestsPass)]
    [InlineData(BenchmarkGradingKind.ArtifactPresent)]
    public async Task Resolves_the_oracle_named_by_the_requested_kind(BenchmarkGradingKind kind)
    {
        var oracle = new FakeGrader(Pass);
        var grader = Build(new FakeResolver(new WorkspaceRequest { RepositoryUrl = "file:///r" }), oracle);

        await grader.GradeAsync(Guid.NewGuid(), Guid.NewGuid(), "b", Spec(kind), 30, CancellationToken.None);

        oracle.ResolvedKind.ShouldBe(kind, "the adapter resolves the registry oracle by the requested kind — TestsPass by default, ArtifactPresent when the model authored it");
        oracle.Context!.Task.TestCommand.ShouldBe(Command, "the authored paths/argv flow to whichever oracle was resolved");
    }

    [Fact]
    public async Task Returns_the_oracles_verdict_verbatim()
    {
        var grade = await Build(new FakeResolver(new WorkspaceRequest { RepositoryUrl = "file:///r" }),
            new FakeGrader(new BenchmarkGrade { Passed = true, Detail = "tests-passed" }))
            .GradeAsync(Guid.NewGuid(), Guid.NewGuid(), "b", Spec(), 30, CancellationToken.None);

        grade.Passed.ShouldBeTrue();
        grade.Detail.ShouldBe("tests-passed");
    }

    [Fact]
    public async Task Removes_the_clone_on_the_happy_path()
    {
        var handle = new FakeHandle("/tmp/clone");
        await BuildWithHandle(handle, new FakeGrader(Pass)).GradeAsync(Guid.NewGuid(), Guid.NewGuid(), "b", Spec(), 30, CancellationToken.None);

        handle.Disposed.ShouldBeTrue("the clone is removed after grading");
    }

    [Fact]
    public async Task A_grade_that_cannot_run_fails_closed_and_still_removes_the_clone()
    {
        var handle = new FakeHandle("/tmp/clone");
        // A model-authored command that can't be run (e.g. a missing binary surfaces as a non-WorkspaceException) is
        // NOT a verdict and NOT a crash — acceptance can't be verified, so it fails closed to "not accepted".
        var grader = BuildWithHandle(handle, new FakeGrader(new InvalidOperationException("command not found")));

        var grade = await grader.GradeAsync(Guid.NewGuid(), Guid.NewGuid(), "b", Spec(), 30, CancellationToken.None);

        grade.Passed.ShouldBeFalse("a check that can't run is not a silent pass — fail closed");
        grade.Detail.ShouldContain("grade-error");
        handle.Disposed.ShouldBeTrue("the clone is removed even when the grade fails");
    }

    [Fact]
    public async Task A_cancellation_during_grading_propagates_and_still_removes_the_clone()
    {
        var handle = new FakeHandle("/tmp/clone");
        var grader = BuildWithHandle(handle, new FakeGrader(new OperationCanceledException()));

        // Cancellation is the one thing NOT swallowed — the caller asked to stop, so it propagates (the clone still cleans up).
        await Should.ThrowAsync<OperationCanceledException>(() => grader.GradeAsync(Guid.NewGuid(), Guid.NewGuid(), "b", Spec(), 30, CancellationToken.None));

        handle.Disposed.ShouldBeTrue("the clone is removed even when grading is cancelled");
    }

    [Fact]
    public async Task A_null_clone_request_fails_closed()
    {
        var grade = await Build(new FakeResolver(clone: null), new FakeGrader(Pass))
            .GradeAsync(Guid.NewGuid(), Guid.NewGuid(), "b", Spec(), 30, CancellationToken.None);

        grade.Passed.ShouldBeFalse("a repo that resolves to no clone cannot be verified → not accepted");
        grade.Detail.ShouldContain("clone-failed");
    }

    [Fact]
    public async Task A_resolve_failure_fails_closed_without_throwing()
    {
        var grade = await Build(new FakeResolver(throwOnResolve: new WorkspaceException("repo 404")), new FakeGrader(Pass))
            .GradeAsync(Guid.NewGuid(), Guid.NewGuid(), "b", Spec(), 30, CancellationToken.None);

        grade.Passed.ShouldBeFalse();
        grade.Detail.ShouldContain("clone-failed");
        grade.Detail.ShouldContain("repo 404", Case.Insensitive, "the failure reason is legible to the operator");
    }

    [Fact]
    public async Task A_clone_failure_fails_closed_without_throwing()
    {
        var grader = Build(new FakeResolver(new WorkspaceRequest { RepositoryUrl = "file:///r" }), new FakeGrader(Pass),
            throwOnPrepare: new WorkspaceException("clone exit 128"));

        var grade = await grader.GradeAsync(Guid.NewGuid(), Guid.NewGuid(), "b", Spec(), 30, CancellationToken.None);

        grade.Passed.ShouldBeFalse();
        grade.Detail.ShouldContain("clone exit 128", Case.Insensitive);
    }

    // ── S3: GradeBaseAsync — the base tree's OWN health under the same oracle (no candidate work) ────

    [Fact]
    public async Task GradeBaseAsync_clones_detached_at_the_base_and_grades_without_applying_anything()
    {
        var resolver = new FakeResolver(new WorkspaceRequest { RepositoryUrl = "file:///r" });
        var oracle = new FakeGrader(Pass);
        var runners = new ScriptedApplyRunnerRegistry(applySucceeds: true);
        var grader = Build(resolver, oracle, runners: runners);

        var grade = await grader.GradeBaseAsync(Guid.NewGuid(), Guid.NewGuid(), "deadbeef", Spec(), 30, CancellationToken.None);

        grade.Passed.ShouldBeTrue();
        runners.Invocations.ShouldContain(i => i.Args.Contains("checkout") && i.Args.Contains("--detach") && i.Args.Contains("deadbeef"), "the S1 base is checked out detached, exactly like the patch twin");
        runners.Invocations.ShouldNotContain(i => i.Args.Contains("apply"), "the baseline measures the BASE — no candidate work is ever applied");
        oracle.Context.ShouldNotBeNull("the same oracle runs against the untouched base tree");
    }

    [Fact]
    public async Task GradeBaseAsync_fails_closed_with_a_prefixed_detail_when_the_base_cannot_be_cloned()
    {
        var runners = new ScriptedApplyRunnerRegistry(applySucceeds: true, cloneSucceeds: false);
        var grader = Build(new FakeResolver(new WorkspaceRequest { RepositoryUrl = "file:///r" }), new FakeGrader(Pass), runners: runners);

        var grade = await grader.GradeBaseAsync(Guid.NewGuid(), Guid.NewGuid(), "deadbeef", Spec(), 30, CancellationToken.None);

        grade.Passed.ShouldBeFalse();
        grade.Detail.ShouldStartWith("clone-failed:", customMessage: "the prefix is the interim infra-vs-genuine discriminator a differential consumer keys on (until F0's typed dispositions)");
    }

    // ── S2: GradePatchAsync — the branch-less twin (a fresh clone at the BASE SHA + apply, no push) ────

    [Fact]
    public async Task GradePatchAsync_clones_at_the_base_sha_not_a_branch()
    {
        var resolver = new FakeResolver(new WorkspaceRequest { RepositoryUrl = "file:///r" });
        var runners = new ScriptedApplyRunnerRegistry(applySucceeds: true);
        var grader = Build(resolver, new FakeGrader(Pass), runners: runners);

        var repoId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        await grader.GradePatchAsync(repoId, teamId, "deadbeef", "diff --git a/x b/x", null, Spec(), 30, CancellationToken.None);

        resolver.RepositoryId.ShouldBe(repoId);
        resolver.TeamId.ShouldBe(teamId);
        // git clone --branch (the resolver-driven path GradeAsync uses) can never check out a raw commit SHA, so
        // the base is checked out via a SEPARATE detached checkout — never threaded through the resolver's ref.
        resolver.Ref.ShouldBeNull("a base SHA is never passed as the branch-oriented resolver ref");
        runners.Invocations.ShouldContain(i => i.Args.Contains("checkout") && i.Args.Contains("--detach") && i.Args.Contains("deadbeef"), "the base SHA is checked out detached after a full clone");
    }

    [Fact]
    public async Task GradePatchAsync_resolves_an_offloaded_patch_team_scoped_before_applying()
    {
        var offloader = new FakeOffloader();
        var artifactId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        offloader.Store(artifactId, teamId, "diff --git a/x b/x\n");

        var runners = new ScriptedApplyRunnerRegistry(applySucceeds: true);
        var grader = Build(new FakeResolver(new WorkspaceRequest { RepositoryUrl = "file:///r" }), new FakeGrader(Pass), runners: runners, offloader: offloader);

        var grade = await grader.GradePatchAsync(Guid.NewGuid(), teamId, "base", "", artifactId, Spec(), 30, CancellationToken.None);

        grade.Passed.ShouldBeTrue();
        offloader.Resolved.ShouldContain((teamId, (Guid?)artifactId), "the offloaded patch is resolved under the SAME team the grade runs for");
    }

    [Fact]
    public async Task GradePatchAsync_grades_the_workspace_after_a_successful_apply()
    {
        var oracle = new FakeGrader(Pass);
        var runners = new ScriptedApplyRunnerRegistry(applySucceeds: true);
        var grader = Build(new FakeResolver(new WorkspaceRequest { RepositoryUrl = "file:///r" }), oracle, runners: runners);

        var grade = await grader.GradePatchAsync(Guid.NewGuid(), Guid.NewGuid(), "base", "diff --git a/x b/x", null, Spec(), 30, CancellationToken.None);

        grade.Passed.ShouldBeTrue();
        var cloneDirectory = CloneDirectory(runners);
        oracle.Context!.WorkspaceDirectory.ShouldBe(cloneDirectory, "the SAME oracle tail GradeAsync uses grades the post-apply working tree");
    }

    [Fact]
    public async Task GradePatchAsync_removes_the_clone_on_the_happy_path()
    {
        var runners = new ScriptedApplyRunnerRegistry(applySucceeds: true);
        await Build(new FakeResolver(new WorkspaceRequest { RepositoryUrl = "file:///r" }), new FakeGrader(Pass), runners: runners)
            .GradePatchAsync(Guid.NewGuid(), Guid.NewGuid(), "base", "diff", null, Spec(), 30, CancellationToken.None);

        Directory.Exists(CloneDirectory(runners)).ShouldBeFalse("the clone directory is removed after grading, exactly like the branch-based path's handle disposal");
    }

    [Fact]
    public async Task GradePatchAsync_fails_closed_when_the_patch_resolves_to_nothing()
    {
        // Neither an inline patch nor a resolvable artifact (missing / cross-team) — nothing to apply.
        var grade = await Build(new FakeResolver(new WorkspaceRequest { RepositoryUrl = "file:///r" }), new FakeGrader(Pass), runners: new ScriptedApplyRunnerRegistry(applySucceeds: true))
            .GradePatchAsync(Guid.NewGuid(), Guid.NewGuid(), "base", "", null, Spec(), 30, CancellationToken.None);

        grade.Passed.ShouldBeFalse("an empty/unresolvable patch is nothing to verify — fail closed, never a silent pass");
        grade.Detail.ShouldBe("no-branch-or-repo", "the SAME detail the branch-less path uses when there is genuinely nothing to grade");
    }

    [Fact]
    public async Task GradePatchAsync_fails_closed_when_the_patch_does_not_apply_onto_its_recorded_base()
    {
        var grade = await Build(new FakeResolver(new WorkspaceRequest { RepositoryUrl = "file:///r" }), new FakeGrader(Pass),
                runners: new ScriptedApplyRunnerRegistry(applySucceeds: false, stderr: "error: patch does not apply"))
            .GradePatchAsync(Guid.NewGuid(), Guid.NewGuid(), "base", "diff --git a/x b/x", null, Spec(), 30, CancellationToken.None);

        grade.Passed.ShouldBeFalse();
        grade.Detail.ShouldContain("patch-apply-failed");
        grade.Detail.ShouldContain("patch does not apply", Case.Insensitive, "git's own stderr is legible to the operator");
    }

    [Fact]
    public async Task GradePatchAsync_never_stages_or_pushes_read_only_by_construction()
    {
        var runners = new ScriptedApplyRunnerRegistry(applySucceeds: true);
        await Build(new FakeResolver(new WorkspaceRequest { RepositoryUrl = "file:///r" }), new FakeGrader(Pass), runners: runners)
            .GradePatchAsync(Guid.NewGuid(), Guid.NewGuid(), "base", "diff --git a/x b/x", null, Spec(), 30, CancellationToken.None);

        var apply = runners.Invocations.Single(i => i.Args.Contains("apply"));
        apply.Args.ShouldNotContain("--index", "no stage — this grade is read-only, nothing is ever committed");
        runners.Invocations.ShouldNotContain(i => i.Args.Contains("push"), "no push — the clone is graded then discarded");
        runners.Invocations.ShouldNotContain(i => i.Args.Contains("commit"), "no commit — the clone is graded then discarded");
    }

    [Fact]
    public async Task GradePatchAsync_a_null_clone_request_fails_closed()
    {
        var grade = await Build(new FakeResolver(clone: null), new FakeGrader(Pass), runners: new ScriptedApplyRunnerRegistry(applySucceeds: true))
            .GradePatchAsync(Guid.NewGuid(), Guid.NewGuid(), "base", "diff", null, Spec(), 30, CancellationToken.None);

        grade.Passed.ShouldBeFalse();
        grade.Detail.ShouldContain("clone-failed");
    }

    [Fact]
    public async Task GradePatchAsync_a_clone_command_failure_fails_closed_without_throwing()
    {
        var grade = await Build(new FakeResolver(new WorkspaceRequest { RepositoryUrl = "file:///r" }), new FakeGrader(Pass),
                runners: new ScriptedApplyRunnerRegistry(applySucceeds: true, cloneSucceeds: false))
            .GradePatchAsync(Guid.NewGuid(), Guid.NewGuid(), "base", "diff --git a/x b/x", null, Spec(), 30, CancellationToken.None);

        grade.Passed.ShouldBeFalse("git clone itself failing (not just resolving to no request) still fails closed");
        grade.Detail.ShouldContain("clone-failed");
    }

    [Fact]
    public async Task GradePatchAsync_a_cancellation_propagates_and_still_removes_the_clone()
    {
        var runners = new ScriptedApplyRunnerRegistry(applySucceeds: true);
        var grader = Build(new FakeResolver(new WorkspaceRequest { RepositoryUrl = "file:///r" }), new FakeGrader(new OperationCanceledException()), runners: runners);

        await Should.ThrowAsync<OperationCanceledException>(() => grader.GradePatchAsync(Guid.NewGuid(), Guid.NewGuid(), "base", "diff", null, Spec(), 30, CancellationToken.None));

        Directory.Exists(CloneDirectory(runners)).ShouldBeFalse("the clone is removed even when grading is cancelled");
    }

    /// <summary>The destination directory argument of the scripted <c>git clone</c> invocation — <c>GradePatchAsync</c> generates this path itself (a fresh GUID under the shared workspaces root), so tests read it back off the runner rather than injecting it.</summary>
    private static string CloneDirectory(ScriptedApplyRunnerRegistry runners) => runners.Invocations.First(i => i.Args.Contains("clone")).Args[^1];

    // ── P3.1 part 2: SetupCommand — an optional workspace-prep step run BEFORE the check ─────────────

    [Fact]
    public async Task A_present_setup_command_runs_before_the_grade_in_the_same_workspace()
    {
        var runners = new ScriptedSetupRunnerRegistry(SandboxStatus.Success);
        var oracle = new FakeGrader(Pass);
        var grader = Build(new FakeResolver(new WorkspaceRequest { RepositoryUrl = "file:///r" }), oracle, handleDir: "/tmp/clone-xyz", runners: runners);

        var spec = new SupervisorAcceptanceSpec { Command = Command, SetupCommand = new[] { "npm", "install" } };
        var grade = await grader.GradeAsync(Guid.NewGuid(), Guid.NewGuid(), "b", spec, 30, CancellationToken.None);

        grade.Passed.ShouldBeTrue("setup succeeded, so the grade proceeds to the oracle's own verdict");
        runners.Invocation.ShouldNotBeNull("the setup command was actually run");
        runners.Invocation!.Command.ShouldBe("npm");
        runners.Invocation.Args.ShouldBe(new[] { "install" });
        runners.Invocation.WorkingDirectory.ShouldBe("/tmp/clone-xyz", "setup runs in the SAME workspace the check grades");
        oracle.Context.ShouldNotBeNull("the oracle still ran after a successful setup");
    }

    [Fact]
    public async Task A_failing_setup_command_fails_closed_without_reaching_the_oracle()
    {
        var runners = new ScriptedSetupRunnerRegistry(SandboxStatus.Failed, exitCode: 1, stderr: "npm ERR! missing script");
        var oracle = new FakeGrader(Pass);
        var grader = Build(new FakeResolver(new WorkspaceRequest { RepositoryUrl = "file:///r" }), oracle, runners: runners);

        var spec = new SupervisorAcceptanceSpec { Command = Command, SetupCommand = new[] { "npm", "install" } };
        var grade = await grader.GradeAsync(Guid.NewGuid(), Guid.NewGuid(), "b", spec, 30, CancellationToken.None);

        grade.Passed.ShouldBeFalse("the check never ran — a failed setup can't be a silent pass");
        grade.Detail.ShouldStartWith("setup-failed:");
        grade.Detail.ShouldContain("npm ERR! missing script", Case.Insensitive, "the setup command's own stderr is legible to the operator");
        oracle.Context.ShouldBeNull("the oracle is never reached when setup fails — the check itself never got a chance to run");
    }

    [Fact]
    public async Task A_timed_out_setup_command_fails_closed_as_setup_timed_out()
    {
        var runners = new ScriptedSetupRunnerRegistry(SandboxStatus.TimedOut, exitCode: -1);
        var oracle = new FakeGrader(Pass);
        var grader = Build(new FakeResolver(new WorkspaceRequest { RepositoryUrl = "file:///r" }), oracle, runners: runners);

        var spec = new SupervisorAcceptanceSpec { Command = Command, SetupCommand = new[] { "npm", "install" } };
        var grade = await grader.GradeAsync(Guid.NewGuid(), Guid.NewGuid(), "b", spec, 30, CancellationToken.None);

        grade.Passed.ShouldBeFalse();
        grade.Detail.ShouldBe("setup-timed-out", "distinct from a plain setup exit failure — the SAME distinction TestsPassGrader draws for the check itself");
        oracle.Context.ShouldBeNull();
    }

    [Fact]
    public void Setup_failure_and_timeout_details_are_infra_classified_regardless_of_work_present()
    {
        AgentAcceptanceContract.IsInfraFailure("setup-failed: npm ERR!", workPresent: true).ShouldBeTrue();
        AgentAcceptanceContract.IsInfraFailure("setup-failed: npm ERR!", workPresent: false).ShouldBeTrue();
        AgentAcceptanceContract.IsInfraFailure("setup-timed-out", workPresent: true).ShouldBeTrue();
        AgentAcceptanceContract.IsInfraFailure("setup-timed-out", workPresent: false).ShouldBeTrue();
    }

    [Theory]
    [InlineData("repo 'web': grade-error: judge binary missing", true)]      // executor multi-repo crash wrap (AgentRunExecutor :1310)
    [InlineData("repo 'web': clone-failed: connection refused", true)]       // wrapped grader detail (:1316 / Rehydrate :730)
    [InlineData("repo 'api': setup-failed: npm ERR!", true)]
    [InlineData("repo 'api': tests-timed-out", true)]
    [InlineData("repo 'web': tests-failed-exit-1", false)]                   // genuine failure stays genuine under the tag
    public void A_repo_tag_never_defeats_the_infra_classification(string detail, bool expected)
    {
        // The multi-repo grade paths wrap the classifiable detail in a uniform "repo 'alias': " tag for display —
        // classification must see through it, or a grader crash on one repo reads as a genuine test failure and
        // buys retries no retry can fix.
        AgentAcceptanceContract.IsInfraFailure(detail, workPresent: false).ShouldBe(expected);
    }

    [Fact]
    public void A_tagged_no_branch_or_repo_keeps_its_work_present_semantics()
    {
        AgentAcceptanceContract.IsInfraFailure("repo 'web': no-branch-or-repo", workPresent: true).ShouldBeTrue();
        AgentAcceptanceContract.IsInfraFailure("repo 'web': no-branch-or-repo", workPresent: false).ShouldBeFalse();
    }

    [Fact]
    public void A_repo_tagged_infra_detail_classifies_InfraUnknown_in_the_typed_layer()
    {
        // The F0 mapping rides the same classifier — a wrapped infra fault must never reach the typed layer as Failed.
        CodeSpace.Core.Services.Supervisor.VerificationDispositions.Classify(false, "repo 'web': grade-error: boom", workPresent: false)
            .ShouldBe(CodeSpace.Messages.Contracts.VerificationDisposition.InfraUnknown);
    }

    // ── The corpus-stub factory the adapter reuses ───────────────────────────────────

    [Theory]
    [InlineData(15)]
    [InlineData(600)]
    public void ForCommand_carries_the_argv_timeout_and_pins_the_tests_pass_grading(int timeout)
    {
        var context = BenchmarkGradingContext.ForCommand(new[] { "pytest", "-q" }, timeout, "/work", new StubRunner());

        context.Task.TestCommand.ShouldBe(new[] { "pytest", "-q" });
        context.Task.TimeoutSeconds.ShouldBe(timeout);
        context.Task.Grading.ShouldBe(BenchmarkGradingKind.TestsPass);
        context.WorkspaceDirectory.ShouldBe("/work");
        context.Runner.ShouldBeOfType<StubRunner>();
    }

    // ── Builders ─────────────────────────────────────────────────────────────────────

    private static readonly BenchmarkGrade Pass = new() { Passed = true, Detail = "tests-passed" };

    private static SupervisorAcceptanceGrader Build(FakeResolver resolver, FakeGrader oracle, string handleDir = "/tmp/clone", WorkspaceException? throwOnPrepare = null, ISandboxRunnerRegistry? runners = null, IArtifactOffloader? offloader = null) =>
        new(resolver, new FakeProviderRegistry(new FakeProvider(new FakeHandle(handleDir), throwOnPrepare)),
            runners ?? new FakeRunnerRegistry(), new FakeGraderRegistry(oracle), offloader ?? new FakeOffloader(), NullLogger<SupervisorAcceptanceGrader>.Instance);

    private static SupervisorAcceptanceGrader BuildWithHandle(FakeHandle handle, FakeGrader oracle) =>
        new(new FakeResolver(new WorkspaceRequest { RepositoryUrl = "file:///r" }), new FakeProviderRegistry(new FakeProvider(handle, null)),
            new FakeRunnerRegistry(), new FakeGraderRegistry(oracle), new FakeOffloader(), NullLogger<SupervisorAcceptanceGrader>.Instance);

    // ── Fakes ────────────────────────────────────────────────────────────────────────

    private sealed class FakeResolver : IAgentWorkspaceResolver
    {
        private readonly WorkspaceRequest? _clone;
        private readonly WorkspaceException? _throwOnResolve;

        public FakeResolver(WorkspaceRequest? clone = null, WorkspaceException? throwOnResolve = null) { _clone = clone; _throwOnResolve = throwOnResolve; }

        public Guid RepositoryId { get; private set; }
        public Guid TeamId { get; private set; }
        public string? Ref { get; private set; }

        public Task<WorkspaceProvisionRequest?> ResolveAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public bool SoftFallback { get; private set; }

        public Task<WorkspaceRequest?> ResolveByRepositoryIdAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken, string? @ref = null, bool softFallback = false, string? pinnedSha = null)
        {
            RepositoryId = repositoryId; TeamId = teamId; Ref = @ref; SoftFallback = softFallback;
            if (_throwOnResolve != null) throw _throwOnResolve;
            return Task.FromResult(_clone);
        }
    }

    private sealed class FakeProviderRegistry : IWorkspaceProviderRegistry
    {
        private readonly FakeProvider _provider;
        public FakeProviderRegistry(FakeProvider provider) => _provider = provider;
        public IWorkspaceProvider Resolve(string kind) => _provider;
        public IReadOnlyList<IWorkspaceProvider> All => new IWorkspaceProvider[] { _provider };
    }

    private sealed class FakeProvider : IWorkspaceProvider
    {
        private readonly FakeHandle _handle;
        private readonly WorkspaceException? _throwOnPrepare;
        public FakeProvider(FakeHandle handle, WorkspaceException? throwOnPrepare) { _handle = handle; _throwOnPrepare = throwOnPrepare; }

        public string Kind => "local";

        public Task<IWorkspaceHandle> PrepareAsync(WorkspaceProvisionRequest request, CancellationToken cancellationToken)
        {
            if (_throwOnPrepare != null) throw _throwOnPrepare;
            return Task.FromResult<IWorkspaceHandle>(_handle);
        }
    }

    private sealed class FakeHandle : IWorkspaceHandle
    {
        public FakeHandle(string directory) => Directory = directory;

        public string Directory { get; }
        public bool Disposed { get; private set; }
        public string PrimaryAlias => "repo";
        public IReadOnlyList<WorkspaceRepositoryHandle> Repositories => Array.Empty<WorkspaceRepositoryHandle>();
        public Task<WorkspaceChanges> CaptureChangesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkspaceChanges> CaptureChangesAsync(string alias, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class FakeRunnerRegistry : ISandboxRunnerRegistry
    {
        public ISandboxRunner Resolve(string kind) => new StubRunner();
        public IReadOnlyList<ISandboxRunner> All => new ISandboxRunner[] { new StubRunner() };
    }

    private sealed class StubRunner : ISandboxRunner
    {
        public string Kind => "local";
        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken) => throw new NotSupportedException("the fake grader never runs the runner");
    }

    private sealed class FakeGraderRegistry : IBenchmarkGraderRegistry
    {
        private readonly FakeGrader _grader;
        public FakeGraderRegistry(FakeGrader grader) => _grader = grader;
        public IBenchmarkGrader Resolve(BenchmarkGradingKind kind) { _grader.ResolvedKind = kind; return _grader; }
    }

    private sealed class FakeGrader : IBenchmarkGrader
    {
        private readonly BenchmarkGrade? _grade;
        private readonly Exception? _throw;

        public FakeGrader(BenchmarkGrade grade) => _grade = grade;
        public FakeGrader(Exception toThrow) => _throw = toThrow;

        public BenchmarkGradingKind Kind => BenchmarkGradingKind.TestsPass;
        public BenchmarkGradingKind ResolvedKind { get; set; }
        public BenchmarkGradingContext? Context { get; private set; }

        public Task<BenchmarkGrade> GradeAsync(BenchmarkGradingContext context, CancellationToken cancellationToken)
        {
            Context = context;
            if (_throw != null) throw _throw;
            return Task.FromResult(_grade!);
        }
    }

    /// <summary>Inline text passes through; an artifact id resolves to whatever was <see cref="Store"/>d for it (team-scoped, mirroring the real offloader's cross-team gate), else empty. Records every (team, artifactId) it was asked to resolve.</summary>
    private sealed class FakeOffloader : IArtifactOffloader
    {
        private readonly Dictionary<Guid, (Guid Team, string Text)> _store = new();
        public List<(Guid Team, Guid? ArtifactId)> Resolved { get; } = new();

        public void Store(Guid artifactId, Guid team, string text) => _store[artifactId] = (team, text);

        public Task<string> ResolveAsync(Guid teamId, string? inline, Guid? artifactId, CancellationToken cancellationToken)
        {
            Resolved.Add((teamId, artifactId));

            if (artifactId is null) return Task.FromResult(inline ?? "");

            return Task.FromResult(_store.TryGetValue(artifactId.Value, out var e) && e.Team == teamId ? e.Text : "");
        }

        public Task<OffloadedText> OffloadIfLargeAsync(Guid teamId, string? text, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the grader never offloads");
    }

    /// <summary>A runner whose <c>git clone</c>/<c>apply</c> outcomes are independently scripted — the seam <see cref="SupervisorAcceptanceGrader.GradePatchAsync"/>'s clone-at-base-SHA + patch application drive, unlike <see cref="StubRunner"/> (which the branch-based path never actually calls). Checkout always succeeds (a scripted checkout failure is covered at the real-git integration tier).</summary>
    private sealed class ScriptedApplyRunnerRegistry : ISandboxRunnerRegistry
    {
        private readonly ScriptedApplyRunner _runner;
        public ScriptedApplyRunnerRegistry(bool applySucceeds, string stderr = "", bool cloneSucceeds = true) => _runner = new ScriptedApplyRunner(applySucceeds, stderr, cloneSucceeds);
        public ISandboxRunner Resolve(string kind) => _runner;
        public IReadOnlyList<ISandboxRunner> All => new ISandboxRunner[] { _runner };
        public IReadOnlyList<SandboxSpec> Invocations => _runner.Invocations;
    }

    private sealed class ScriptedApplyRunner : ISandboxRunner
    {
        private readonly bool _applySucceeds;
        private readonly string _stderr;
        private readonly bool _cloneSucceeds;

        public ScriptedApplyRunner(bool applySucceeds, string stderr, bool cloneSucceeds) { _applySucceeds = applySucceeds; _stderr = stderr; _cloneSucceeds = cloneSucceeds; }

        public List<SandboxSpec> Invocations { get; } = new();
        public string Kind => "local";

        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
        {
            Invocations.Add(spec);

            if (spec.Args.Contains("clone"))
            {
                // Mimic git's own side effect (creating the destination directory) so the REAL patch-file write
                // ApplyPatchAsync performs next lands somewhere that genuinely exists on disk.
                if (_cloneSucceeds) Directory.CreateDirectory(spec.Args[^1]);

                return Task.FromResult(_cloneSucceeds
                    ? new SandboxResult { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "", Stderr = "" }
                    : new SandboxResult { Status = SandboxStatus.Failed, ExitCode = 128, Stdout = "", Stderr = "fatal: could not read from remote repository" });
            }

            if (spec.Args.Contains("checkout"))
                return Task.FromResult(new SandboxResult { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "", Stderr = "" });

            return Task.FromResult(_applySucceeds
                ? new SandboxResult { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "", Stderr = "" }
                : new SandboxResult { Status = SandboxStatus.Failed, ExitCode = 1, Stdout = "", Stderr = _stderr });
        }
    }

    /// <summary>A single-invocation scripted runner for the SetupCommand tests — the branch-based <see cref="SupervisorAcceptanceGrader.GradeAsync"/> path only ever calls the runner directly for the setup step (the check itself is graded by the faked <see cref="FakeGrader"/> oracle, which never touches the runner).</summary>
    private sealed class ScriptedSetupRunnerRegistry : ISandboxRunnerRegistry
    {
        private readonly ScriptedSetupRunner _runner;
        public ScriptedSetupRunnerRegistry(SandboxStatus status, int exitCode = 0, string stderr = "") => _runner = new ScriptedSetupRunner(status, exitCode, stderr);
        public ISandboxRunner Resolve(string kind) => _runner;
        public IReadOnlyList<ISandboxRunner> All => new ISandboxRunner[] { _runner };
        public SandboxSpec? Invocation => _runner.Invocation;
    }

    private sealed class ScriptedSetupRunner : ISandboxRunner
    {
        private readonly SandboxStatus _status;
        private readonly int _exitCode;
        private readonly string _stderr;

        public ScriptedSetupRunner(SandboxStatus status, int exitCode, string stderr) { _status = status; _exitCode = exitCode; _stderr = stderr; }

        public string Kind => "local";
        public SandboxSpec? Invocation { get; private set; }

        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
        {
            Invocation = spec;
            return Task.FromResult(new SandboxResult { Status = _status, ExitCode = _exitCode, Stdout = "", Stderr = _stderr });
        }
    }
}

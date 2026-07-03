using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Supervisor;
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

    private static SupervisorAcceptanceGrader Build(FakeResolver resolver, FakeGrader oracle, string handleDir = "/tmp/clone", WorkspaceException? throwOnPrepare = null) =>
        new(resolver, new FakeProviderRegistry(new FakeProvider(new FakeHandle(handleDir), throwOnPrepare)),
            new FakeRunnerRegistry(), new FakeGraderRegistry(oracle), NullLogger<SupervisorAcceptanceGrader>.Instance);

    private static SupervisorAcceptanceGrader BuildWithHandle(FakeHandle handle, FakeGrader oracle) =>
        new(new FakeResolver(new WorkspaceRequest { RepositoryUrl = "file:///r" }), new FakeProviderRegistry(new FakeProvider(handle, null)),
            new FakeRunnerRegistry(), new FakeGraderRegistry(oracle), NullLogger<SupervisorAcceptanceGrader>.Instance);

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

        public Task<WorkspaceRequest?> ResolveByRepositoryIdAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken, string? @ref = null, bool softFallback = false)
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
}

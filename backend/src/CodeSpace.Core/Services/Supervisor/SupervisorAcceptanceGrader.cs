using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// Clones a repository at a produced branch and grades it with the shared <see cref="TestsPassGrader"/> oracle —
/// the supervisor's objective acceptance adapter (L4 arc A). It OWNS the clone (a fresh, agent-independent shallow
/// checkout from the remote) and DELEGATES the verdict to the registry-resolved grader, so it reuses both the
/// workspace底座 (<see cref="IAgentWorkspaceResolver"/> + <see cref="IWorkspaceProviderRegistry"/>) and the grading
/// oracle without duplicating either. Scoped because the workspace resolver injects the DbContext; the registries
/// it resolves are singletons. Dormant until A3 folds its verdict at the supervisor's accept boundary.
/// </summary>
public sealed class SupervisorAcceptanceGrader : ISupervisorAcceptanceGrader, IScopedDependency
{
    private const string DefaultRunnerKind = "local";

    private readonly IAgentWorkspaceResolver _workspaceResolver;
    private readonly IWorkspaceProviderRegistry _providers;
    private readonly ISandboxRunnerRegistry _runners;
    private readonly IBenchmarkGraderRegistry _graders;
    private readonly ILogger<SupervisorAcceptanceGrader> _logger;

    public SupervisorAcceptanceGrader(IAgentWorkspaceResolver workspaceResolver, IWorkspaceProviderRegistry providers, ISandboxRunnerRegistry runners, IBenchmarkGraderRegistry graders, ILogger<SupervisorAcceptanceGrader> logger)
    {
        _workspaceResolver = workspaceResolver;
        _providers = providers;
        _runners = runners;
        _graders = graders;
        _logger = logger;
    }

    public async Task<BenchmarkGrade> GradeAsync(Guid repositoryId, Guid teamId, string branch, IReadOnlyList<string> command, int timeoutSeconds, CancellationToken cancellationToken, BenchmarkGradingKind kind = BenchmarkGradingKind.TestsPass)
    {
        try
        {
            var clone = await _workspaceResolver.ResolveByRepositoryIdAsync(repositoryId, teamId, cancellationToken, @ref: branch).ConfigureAwait(false)
                ?? throw new WorkspaceException($"Repository {repositoryId} resolved to no clone request for acceptance grading.");

            await using var workspace = await _providers.Resolve(DefaultRunnerKind).PrepareAsync(WorkspaceProvisionRequest.FromSingle(clone), cancellationToken).ConfigureAwait(false);

            return await GradeWorkspaceAsync(workspace.Directory, command, timeoutSeconds, kind, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceException ex)
        {
            // A repo/branch we cannot clone cannot be verified → fail closed to "not accepted" (never a silent pass).
            _logger.LogWarning(ex, "Acceptance grading could not clone {RepositoryId} at {Branch}; failing closed to not-accepted", repositoryId, branch);
            return Failed($"clone-failed: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The check itself could not be RUN (e.g. a model-authored command names a binary not on PATH) — acceptance
            // still cannot be verified, so fail closed to "not accepted" rather than crashing the supervisor turn. Only a
            // genuine cancellation propagates (the caller asked to stop).
            _logger.LogWarning(ex, "Acceptance grading could not run the check for {RepositoryId} at {Branch}; failing closed to not-accepted", repositoryId, branch);
            return Failed($"grade-error: {ex.Message}");
        }
    }

    private async Task<BenchmarkGrade> GradeWorkspaceAsync(string directory, IReadOnlyList<string> command, int timeoutSeconds, BenchmarkGradingKind kind, CancellationToken cancellationToken)
    {
        var context = BenchmarkGradingContext.ForCommand(command, timeoutSeconds, directory, _runners.Resolve(DefaultRunnerKind));

        return await _graders.Resolve(kind).GradeAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private static BenchmarkGrade Failed(string detail) => new() { Passed = false, Detail = detail };
}

using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Authority;
using CodeSpace.Core.Services.Agents.Authority.Exceptions;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Workspace;

public sealed record LocalAcceptancePreparation(AgentRunOwnerToken Owner, Guid TeamId, AgentTask Task, string RunnerKind, string Directory);
public sealed record LocalAcceptanceRequest(AgentRunOwnerToken Owner, Guid TeamId, AgentTask Task, LocalAcceptanceContext? Context);
public sealed record LocalAcceptanceObservation(string? Directory, BenchmarkGrade? Failure);
/// <summary>Opaque, non-serializable invocation state. Grade accepts only the private implementation minted by PrepareAsync; a serialized task or caller-created subclass cannot manufacture one.</summary>
public abstract class LocalAcceptanceContext : IDisposable
{
    private protected LocalAcceptanceContext() { }
    public abstract void Dispose();
}
/// <summary>Verifies an already selected local invocation world under fresh run authority and ownership. Directory continuity is observed, not a new filesystem access grant. No context is reconstructed from a durable handle after host loss.</summary>
public sealed class LocalAcceptanceVerifier(IAgentRunService runs, ExecutionAuthorityService authority, ISupervisorAcceptanceGrader grader, IArtifactManifestStore manifests, IArtifactStore artifacts) : IScopedDependency
{
    public async Task<LocalAcceptanceContext> PrepareAsync(LocalAcceptancePreparation request, CancellationToken cancellationToken)
    {
        var hash = AgentAcceptanceContract.Hash(request.Task);
        var context = new Context(request.Owner, request.TeamId, hash);
        context.Failure = await CheckScopeAsync(new(request.Owner, request.TeamId, request.Task, context), cancellationToken).ConfigureAwait(false);
        if (context.Failure != null) return context;
        if (request.RunnerKind != SandboxKinds.Local || RepositoryWorkspaceResolver.CanonicalWorkspace(request.Task) != null)
            context.Failure = Failed("local-workspace-unavailable", GradeFailureClass.Environment);
        else if (request.Task.WorkspaceDirectory is { } selected && !SamePath(selected, request.Directory))
            context.Failure = Failed("local-context-mismatch", GradeFailureClass.GraderFault);
        else if (request.Task.Acceptance is not { } spec)
            context.Failure = Failed("missing-acceptance-contract", GradeFailureClass.SpecIncomplete);
        else if (ValidateSpec(spec) is { } invalid)
            context.Failure = invalid;
        else
        {
            // Deep-copy collections and JSON payloads: later mutations of a caller's list cannot change the frozen judge.
            context.Spec = JsonSerializer.Deserialize<SupervisorAcceptanceSpec>(JsonSerializer.Serialize(spec, AgentJson.Options), AgentJson.Options)!;
            try { context.Workspace = await LocalAcceptanceWorkspace.CaptureAsync(request.Directory, context.Spec.OraclePaths ?? [], cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException or EntryPointNotFoundException)
            {
                context.Failure = Failed("local-workspace-unavailable", GradeFailureClass.Environment);
            }
        }
        return context;
    }

    public async Task<BenchmarkGrade> GradeAsync(LocalAcceptanceRequest request, CancellationToken cancellationToken)
    {
        var observation = await ObserveAsync(request, cancellationToken).ConfigureAwait(false);
        if (observation.Failure != null) return observation.Failure;
        var context = (Context)request.Context!;
        BenchmarkGrade grade;
        try { grade = await grader.GradeDirectoryAsync(observation.Directory!, context.Spec!, request.TeamId, context.Spec!.TimeoutSeconds ?? SupervisorLane.AcceptanceGradeTimeoutSeconds, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException and not Exceptions.AgentRunOwnershipLostException and not AgentAuthorityDeniedException)
        {
            return Failed($"local-oracle-unavailable: {ex.GetType().Name}", GradeFailureClass.GraderFault);
        }
        var after = await ObserveAsync(request, cancellationToken).ConfigureAwait(false);
        if (after.Failure != null) return after.Failure;
        if (grade.Passed && await CheckDeclaredReceiptsAsync(context, cancellationToken).ConfigureAwait(false) is { } captureFailure) return captureFailure;
        if (await CheckScopeAsync(request, cancellationToken).ConfigureAwait(false) is { } scopeFailure) return scopeFailure;
        return grade with { OracleNote = context.Spec!.OraclePaths is { Count: > 0 } ? "Explicit oracle file digests unchanged at pre/post observations; path-based execution is not pinned." : "No oracle file snapshot was declared; exact argv was executed without inferred dependency protection." };
    }

    /// <summary>
    /// Cover each exact declared logical path with a current receipt from this run/team/owner epoch and the same
    /// observed regular-file bytes. Exact duplicates coalesce, while two paths may share one CAS object. Metadata
    /// is not a fresh physical storage read; the receipt records the capture write, not arbitrary future availability.
    /// </summary>
    private async Task<BenchmarkGrade?> CheckDeclaredReceiptsAsync(Context context, CancellationToken cancellationToken)
    {
        if (!AgentAcceptanceContract.GradesFromDeliverables(context.Spec)) return null;
        try
        {
            var rows = await manifests.ListForAgentRunAsync(context.Owner.RunId, context.TeamId, cancellationToken).ConfigureAwait(false);
            foreach (var path in context.Spec!.Command.Distinct(StringComparer.Ordinal))
            {
                var current = rows.Where(row => row.AgentRunId == context.Owner.RunId && row.TeamId == context.TeamId && row.FenceEpoch == context.Owner.Epoch && row.SupersededByManifestId == null && row.LogicalPath == path).ToList();
                if (current.Count != 1) return Failed("declared-deliverable-receipt-missing", GradeFailureClass.GraderFault);
                var receipt = current[0];
                var file = await LocalAcceptanceWorkspace.ObserveFileAsync(context.Workspace!.Directory, path, cancellationToken).ConfigureAwait(false);
                var metadata = await artifacts.GetMetadataAsync(context.TeamId, receipt.ContentArtifactId, cancellationToken).ConfigureAwait(false);
                if (metadata == null || metadata.SizeBytes != receipt.SizeBytes || !string.Equals(metadata.Sha256, receipt.Sha256, StringComparison.OrdinalIgnoreCase))
                    return Failed("declared-deliverable-receipt-unavailable", GradeFailureClass.GraderFault);
                if (file.SizeBytes != receipt.SizeBytes || !string.Equals(file.Sha256, receipt.Sha256, StringComparison.OrdinalIgnoreCase))
                    return Failed("declared-deliverable-bytes-changed", GradeFailureClass.GraderFault);
            }
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not Exceptions.AgentRunOwnershipLostException and not AgentAuthorityDeniedException)
        {
            return Failed($"declared-deliverable-unverifiable: {ex.GetType().Name}", GradeFailureClass.GraderFault);
        }
    }

    /// <summary>Observe the same admitted context before declared artifact capture. This does not authorize an undeclared directory walk or transfer ownership of the directory.</summary>
    public async Task<LocalAcceptanceObservation> ObserveAsync(LocalAcceptanceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Context is null) return new(null, Failed("no-live-local-context", GradeFailureClass.Environment));
        if (request.Context is not Context context || context.Owner != request.Owner || context.TeamId != request.TeamId || context.ContractHash != AgentAcceptanceContract.Hash(request.Task))
            return new(null, Failed("local-context-mismatch", GradeFailureClass.GraderFault));
        if (await CheckScopeAsync(request, cancellationToken).ConfigureAwait(false) is { } scopeFailure) return new(null, scopeFailure);
        if (context.Disposed) return new(null, Failed("workspace-context-disposed", GradeFailureClass.Environment));
        if (context.Failure != null) return new(null, context.Failure);
        if (await context.Workspace!.CheckAsync(cancellationToken).ConfigureAwait(false) is { } failure) return new(null, Failed(failure, GradeFailureClass.GraderFault));
        return new(context.Workspace.Directory, null);
    }

    private async Task<BenchmarkGrade?> CheckScopeAsync(LocalAcceptanceRequest request, CancellationToken cancellationToken)
    {
        await authority.EnsureAgentActionAsync(request.Owner.RunId, request.TeamId, cancellationToken).ConfigureAwait(false);
        await runs.AssertOwnershipAsync(request.Owner, cancellationToken).ConfigureAwait(false);
        var run = await runs.GetAsync(request.Owner.RunId, cancellationToken).ConfigureAwait(false);
        var task = JsonSerializer.Deserialize<AgentTask>(run.TaskJson, AgentJson.Options);
        if (run.TeamId != request.TeamId || task == null || AgentAcceptanceContract.Hash(task) != AgentAcceptanceContract.Hash(request.Task)
            || task.WorkspaceDirectory is { } stored && !SamePath(stored, request.Task.WorkspaceDirectory))
            return Failed("local-context-mismatch", GradeFailureClass.GraderFault);
        return null;
    }

    internal static BenchmarkGrade? ValidateSpec(SupervisorAcceptanceSpec spec)
    {
        if (spec.ProtectedPaths is { Count: > 0 }) return Failed("local-protected-pathspec-unsupported", GradeFailureClass.SpecIncomplete);
        return ValidateContract(spec);
    }

    internal static BenchmarkGrade? ValidateContract(SupervisorAcceptanceSpec spec)
    {
        if (spec.Kind is { } kind && !Enum.IsDefined(kind)) return Failed("unknown-acceptance-kind", GradeFailureClass.SpecIncomplete);
        if (spec.Command == null || spec.Kind is null or BenchmarkGradingKind.TestsPass && !ValidArgv(spec.Command)) return Failed("invalid-acceptance-argv", GradeFailureClass.SpecIncomplete);
        if (spec.SetupCommand is { Count: > 0 } setup && !ValidArgv(setup)) return Failed("invalid-setup-argv", GradeFailureClass.SpecIncomplete);
        if (AgentAcceptanceContract.ValidateAuthored(spec) is { } invalid) return Failed(invalid, GradeFailureClass.SpecIncomplete);
        return null;
    }

    private static bool ValidArgv(IReadOnlyList<string> argv) => argv.Count > 0 && !string.IsNullOrWhiteSpace(argv[0]) && argv.All(value => value != null && !value.Contains('\0'));
    private static bool SamePath(string left, string? right)
    {
        try { return right != null && Path.GetFullPath(left) == Path.GetFullPath(right); }
        catch (ArgumentException) { return false; }
    }
    private static BenchmarkGrade Failed(string detail, GradeFailureClass failureClass) => new() { Passed = false, Detail = "grade-error: " + detail, Class = failureClass };

    private sealed class Context(AgentRunOwnerToken owner, Guid teamId, string contractHash) : LocalAcceptanceContext
    {
        public AgentRunOwnerToken Owner { get; } = owner;
        public Guid TeamId { get; } = teamId;
        public string ContractHash { get; } = contractHash;
        public SupervisorAcceptanceSpec? Spec { get; set; }
        public LocalAcceptanceWorkspace? Workspace { get; set; }
        public BenchmarkGrade? Failure { get; set; }
        public bool Disposed { get; private set; }
        public override void Dispose() { Disposed = true; Workspace?.Dispose(); }
    }
}

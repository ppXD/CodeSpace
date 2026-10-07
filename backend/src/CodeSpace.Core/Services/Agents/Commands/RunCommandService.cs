using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Core.Settings;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Commands;

public sealed class RunCommandService : IRunCommandService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IProviderAuthResolver _auth;
    private readonly ISandboxRunnerRegistry _runners;
    private readonly IWorkspaceProviderRegistry _workspaces;
    private readonly AgentDefaultRunnerSetting _defaultRunner;
    private readonly CallerCommandLanes _lanes;

    public RunCommandService(CodeSpaceDbContext db, IProviderAuthResolver auth, ISandboxRunnerRegistry runners, IWorkspaceProviderRegistry workspaces, AgentDefaultRunnerSetting defaultRunner, CallerCommandLanes lanes)
    {
        _db = db;
        _auth = auth;
        _runners = runners;
        _workspaces = workspaces;
        _defaultRunner = defaultRunner;
        _lanes = lanes;
    }

    public async Task<SandboxResult> RunAsync(RunCommandRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Command))
            throw new InvalidOperationException("A command is required.");

        await using var lane = await EnterCallerLaneAsync(request.CallerPosture, cancellationToken).ConfigureAwait(false);

        var runnerKind = string.IsNullOrWhiteSpace(request.RunnerKind) ? _defaultRunner.Value : request.RunnerKind;
        var runner = _runners.Resolve(runnerKind);

        // Repo-scoped → clone into a fresh per-run workspace the command runs in; ephemeral → no checkout.
        // The same runnerKind selects the matching workspace provider, so a future docker/k8s pair composes here.
        var workspace = request.RepositoryId is { } repositoryId
            ? await _workspaces.Resolve(runnerKind).PrepareAsync(WorkspaceProvisionRequest.FromSingle(await BuildWorkspaceRequestAsync(repositoryId, request, cancellationToken).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            return await runner.RunAsync(BuildSpec(request, workspace?.Directory), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (workspace != null) await workspace.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A command an agent asked for waits its turn among its run's commands (<see cref="CallerCommandLanes"/>), so the
    /// commands one run has running never hold more than one tier row of cgroup ceilings between them. A workflow
    /// node's command has no calling run and never waits.
    /// </summary>
    private async Task<IAsyncDisposable?> EnterCallerLaneAsync(AgentRunPosture? caller, CancellationToken cancellationToken) =>
        caller is null ? null : await _lanes.EnterAsync(caller.RunId, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// What a command an agent asked network for lost to its calling run's posture, as one line the agent and whoever
    /// approved the call can read on the tool result — or null when nothing was lost: no calling run (a workflow node),
    /// no network asked for (or none the deployment ceiling allows anyway), or granted as asked. Derived from the same
    /// projection <see cref="BuildSpec"/> runs, so it can never disagree with the sandbox the command got. "Off" carries
    /// the confinement caveat: it is severed only where the sandbox confines.
    /// </summary>
    public static string? CallerNetworkNarrowing(RunCommandRequest request)
    {
        if (request.CallerPosture is not { } caller) return null;

        var authored = AuthoredSpec(request, workingDirectory: null);

        if (!authored.AllowNetwork) return null;

        return WithinCallerPosture(authored, caller) switch
        {
            { AllowNetwork: false } when caller.Permissions.Network != AgentNetworkAccess.On => $"off: the calling run ({caller.Autonomy}) has no network{AgentAutonomyPolicy.ConfinementCaveat}",
            { AllowNetwork: false } => $"off: the calling run's egress allowlist names no host a command may reach{AgentAutonomyPolicy.ConfinementCaveat}",
            { EgressAllowlist: { Count: > 0 } hosts } => $"narrowed to the calling run's egress allowlist ({string.Join(", ", hosts)})",
            _ => null,
        };
    }

    /// <summary>
    /// The request → <see cref="SandboxSpec"/> projection, with the deployment autonomy ceiling
    /// (<c>Sandbox:MaxAutonomy</c>) narrowing the requested egress. This lane has NO autonomy tier anywhere in its
    /// vocabulary — <c>agent.run_command</c>'s raw <c>"network": true</c> lands straight on
    /// <see cref="SandboxSpec.AllowNetwork"/> — so the tier clamps that bound the agent lanes cannot reach it, and
    /// the ceiling's own DERIVED network posture (<see cref="AgentAutonomyPolicy.Derive"/>, the same table the
    /// sandbox enforces) has the last word instead. NARROW-ONLY: a ceiling that grants network leaves the request
    /// exactly as asked, so the committed default clamps nothing.
    ///
    /// <para>A command an AGENT asked for (<see cref="RunCommandRequest.CallerPosture"/> set) is then narrowed to that
    /// agent's own run by <see cref="WithinCallerPosture"/>; a workflow node's command is exactly as above.</para>
    ///
    /// <para>Internal (not private) so the narrowing is unit-pinned directly (InternalsVisibleTo) rather than only
    /// through a runner that would have to be confining to show it.</para>
    /// </summary>
    internal static SandboxSpec BuildSpec(RunCommandRequest request, string? workingDirectory) => WithinCallerPosture(AuthoredSpec(request, workingDirectory), request.CallerPosture);

    /// <summary>The command as authored, under the deployment ceiling alone — what a workflow node's command runs as, and what an agent's is narrowed from.</summary>
    private static SandboxSpec AuthoredSpec(RunCommandRequest request, string? workingDirectory) => new()
    {
        Command = request.Command,
        Args = request.Args,
        WorkingDirectory = workingDirectory,
        Environment = request.Environment,
        TimeoutSeconds = request.TimeoutSeconds,
        CaptureBudget = new SandboxCaptureBudget(),
        AllowNetwork = request.AllowNetwork && AgentAutonomyPolicy.Derive(AgentAutonomyPolicy.DeploymentCeiling).Network == AgentNetworkAccess.On,
        MaxProcesses = request.MaxProcesses,
        MaxFileSizeMb = request.MaxFileSizeMb,
    };

    /// <summary>
    /// Narrow a command an agent asked for through its tool fabric to the posture of the agent's OWN run. The command's
    /// sandbox is a sandbox of its own, so without this a network-off agent could hand itself the internet by asking
    /// for <c>"network": true</c>, and every command it ran was uncapped. NARROW-ONLY: the network stays only when the
    /// command asked for it, the deployment ceiling allows it (above) AND the run has it; the ceilings are those of the
    /// run's tier clamped by the deployment ceiling, narrowed by the operator's host memory budget — the same table and
    /// budget <c>AgentRunExecutor.ApplyResourceCeilings</c> holds the run itself to. Those ceilings land on a cgroup leaf
    /// of the command's own, beside the agent's: they bound the command, not the run as a whole, which is why a run's
    /// commands also queue (<see cref="CallerCommandLanes"/>). No caller (a workflow node) ⇒ the spec is returned untouched.
    /// </summary>
    private static SandboxSpec WithinCallerPosture(SandboxSpec spec, AgentRunPosture? caller)
    {
        if (caller is null) return spec;

        var ceilings = AgentAutonomyPolicy.Ceilings(AgentAutonomyPolicy.Clamp(caller.Autonomy, AgentAutonomyPolicy.DeploymentCeiling), RuntimeSettings.Current.AgentMemoryCeilingMb);
        var narrowed = spec with { AllowNetwork = spec.AllowNetwork && caller.Permissions.Network == AgentNetworkAccess.On, MaxMemoryMb = ceilings.MemoryMb, MaxCpuPercent = ceilings.CpuPercent };

        return WithinCallerEgress(narrowed, caller.Permissions);
    }

    /// <summary>
    /// An allowlisted caller's command reaches ONLY the operator's extra hosts (<see cref="AgentPermissions.EgressAllowHosts"/>).
    /// The run's own allowlist adds its model host and its repositories' git hosts; a command needs neither, and a
    /// repository the command names may sit on a host the run never had, so the extra hosts are the one part that is a
    /// strict subset of the run's reach. None ⇒ severed, never full egress — the same fail-closed rule
    /// <c>AgentRunExecutor.ApplyEgressPolicy</c> applies to the run itself.
    /// </summary>
    private static SandboxSpec WithinCallerEgress(SandboxSpec spec, AgentPermissions permissions)
    {
        if (!spec.AllowNetwork || permissions.Egress != AgentEgressPolicy.Allowlist) return spec;

        var hosts = EgressAllowlistBuilder.Build(modelBaseUrl: null, modelProvider: null, Array.Empty<string>(), permissions.EgressAllowHosts);

        return hosts.Count == 0 ? spec with { AllowNetwork = false } : spec with { EgressAllowlist = hosts };
    }

    /// <summary>
    /// Repo → clone request: load the repository (by id, like the git.* node services), resolve a short-lived
    /// token through the same provider auth layer the resolver uses, and reuse its provider→username table so
    /// there's one source of truth. A repo with no bound credential clones anonymously (public / local repo).
    /// </summary>
    private async Task<WorkspaceRequest> BuildWorkspaceRequestAsync(Guid repositoryId, RunCommandRequest request, CancellationToken cancellationToken)
    {
        // Fail-closed tenant scope: the repo is resolved ONLY within the run's team, so a model-supplied /
        // untrusted repositoryId can never clone another tenant's repo. No team context with a repo requested is
        // refused outright. A repo in another team falls out of the filter → the same non-leaking "not found".
        if (request.TeamId is not { } team)
            throw new WorkspaceException("Cannot clone a repository without a team context for the run.");

        var bound = CallerBinding(request.CallerPosture, repositoryId);

        var repo = await _db.Repository
            .Include(r => r.ProviderInstance)
            .Include(r => r.Credential)
            .SingleOrDefaultAsync(r => r.Id == repositoryId && r.TeamId == team && r.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkspaceException($"Repository {repositoryId} not found.");

        if (string.IsNullOrWhiteSpace(repo.CloneUrlHttps))
            throw new WorkspaceException($"Repository {repositoryId} has no HTTPS clone URL to clone from.");

        EnsureRefWithinBinding(bound, request.Ref, repo);

        var token = await ResolveTokenAsync(repo, cancellationToken).ConfigureAwait(false);

        return new WorkspaceRequest
        {
            RepositoryUrl = repo.CloneUrlHttps,
            Ref = string.IsNullOrWhiteSpace(request.Ref) ? repo.DefaultBranch : request.Ref,
            Token = token,
            TokenUsername = token is null ? null : RepositoryWorkspaceResolver.TokenUsernameFor(repo.ProviderInstance.Provider),
        };
    }

    /// <summary>
    /// The calling run's binding of the repository an agent's command names — null for a workflow node's command, which
    /// has no calling run and resolves its authored repository within its team as before. A repository the run is not
    /// bound to is refused with the same "not found" the tenant filter gives, before it is ever loaded.
    /// </summary>
    private static WorkspaceRepositorySpec? CallerBinding(AgentRunPosture? caller, Guid repositoryId)
    {
        if (caller is null) return null;

        return AgentRepositoryBinding.Find(caller, repositoryId) ?? throw new WorkspaceException(AgentRepositoryBinding.NotFound(repositoryId));
    }

    /// <summary>
    /// An agent's command checks out read-only context only at its bound branch or its default branch, so the tree it
    /// builds and runs is the content the operator bound, never a branch the agent named (what the pin does and does not
    /// cover is on <see cref="AgentRepositoryBinding"/>). A refusal rather than an approval card: the binding is the
    /// operator's own narrowing, which a single approver's click should not widen mid-run, and the card cannot show the
    /// ref it would be consenting to. The agent tool refuses it before the call is ever parked for approval too
    /// (<c>NodeAgentTool</c>); this holds a caller that reaches the service directly. A writable repository, and a
    /// workflow node's command (<paramref name="bound"/> null), take any ref.
    /// </summary>
    private static void EnsureRefWithinBinding(WorkspaceRepositorySpec? bound, string? requestedRef, Repository repo)
    {
        if (bound is null || AgentRepositoryBinding.AllowsRef(bound, requestedRef, repo.DefaultBranch)) return;

        throw new WorkspaceException(AgentRepositoryBinding.RefOutsideBinding(repo.Id, bound, requestedRef, repo.DefaultBranch));
    }

    private async Task<string?> ResolveTokenAsync(Repository repo, CancellationToken cancellationToken)
    {
        if (repo.Credential is null) return null;

        var auth = await _auth.ResolveAsync(new ProviderContext(repo.ProviderInstance, repo.Credential), cancellationToken).ConfigureAwait(false);

        return auth.Token;
    }
}

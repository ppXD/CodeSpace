using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Identity;
using CodeSpace.Core.Services.Providers.Scopes;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// Enforces, at the moment a person resolves an interactive wait, that they have a linked identity on
/// every provider a downstream act-as-user node will act AS them on. This is the GENERIC gate behind the
/// chat-driven "review as me" loop: it reads the act-as-user trait off node manifests + the static graph
/// (via <see cref="ActorIdentityRequirementPlan"/>) — nothing about pr_review is hardcoded, so new
/// act-as-user nodes are covered the moment they declare <see cref="ActsAsUserSpec"/>.
///
/// Called from the synchronous respond/resume path, so a missing identity surfaces as
/// <see cref="ActorIdentityRequiredException"/> → HTTP 428, letting the client prompt a link + retry
/// instead of the run failing later in the background.
/// </summary>
public interface IActorIdentityRequirementGate
{
    /// <summary>
    /// Throws <see cref="ActorIdentityRequiredException"/> when <paramref name="responderUserId"/> lacks a
    /// linked identity on a provider that an act-as-user node downstream of <paramref name="waitNodeId"/>
    /// (with its actor input wired to that wait) will act as them on. No-op otherwise — including when the
    /// run / version / repo data is absent (degrade to "let the resume proceed"); only the identity throws.
    /// </summary>
    Task EnsureResponderCanActAsUserAsync(Guid runId, string waitNodeId, Guid responderUserId, CancellationToken cancellationToken);
}

public sealed class ActorIdentityRequirementGate : IActorIdentityRequirementGate, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly INodeRegistry _nodeRegistry;
    private readonly IActorIdentityResolver _actorIdentity;
    private readonly IProviderRegistry _providers;
    private readonly IScopeChecker _scopeChecker;
    private readonly ILogger<ActorIdentityRequirementGate> _logger;

    public ActorIdentityRequirementGate(CodeSpaceDbContext db, INodeRegistry nodeRegistry, IActorIdentityResolver actorIdentity, IProviderRegistry providers, IScopeChecker scopeChecker, ILogger<ActorIdentityRequirementGate> logger)
    {
        _db = db;
        _nodeRegistry = nodeRegistry;
        _actorIdentity = actorIdentity;
        _providers = providers;
        _scopeChecker = scopeChecker;
        _logger = logger;
    }

    public async Task EnsureResponderCanActAsUserAsync(Guid runId, string waitNodeId, Guid responderUserId, CancellationToken cancellationToken)
    {
        var requirements = await DeriveRequirementsAsync(runId, waitNodeId, cancellationToken).ConfigureAwait(false);

        foreach (var requirement in requirements)
            await EnsureLinkedAsync(requirement, responderUserId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ActorIdentityRequirementPlan.Requirement>> DeriveRequirementsAsync(Guid runId, string waitNodeId, CancellationToken cancellationToken)
    {
        var run = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => new { r.WorkflowId, r.WorkflowVersion, r.RunRequestId })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (run == null) return Array.Empty<ActorIdentityRequirementPlan.Requirement>();

        var definitionJson = await _db.WorkflowVersion.AsNoTracking()
            .Where(v => v.WorkflowId == run.WorkflowId && v.Version == run.WorkflowVersion)
            .Select(v => v.DefinitionJson)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        var definition = definitionJson == null ? null : JsonSerializer.Deserialize<WorkflowDefinition>(definitionJson, WorkflowJson.Options);
        if (definition == null) return Array.Empty<ActorIdentityRequirementPlan.Requirement>();

        var payloadJson = await _db.WorkflowRunRequest.AsNoTracking()
            .Where(q => q.Id == run.RunRequestId)
            .Select(q => q.NormalizedPayloadJson)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        var inputScope = BuildInputScope(definition, ParsePayloadObject(payloadJson));

        return ActorIdentityRequirementPlan.Derive(definition, waitNodeId, ActsAsUserOf, inputScope);
    }

    private ActsAsUserSpec? ActsAsUserOf(string typeKey) => _nodeRegistry.Contains(typeKey) ? _nodeRegistry.Resolve(typeKey).Manifest.ActsAsUser : null;

    private async Task EnsureLinkedAsync(ActorIdentityRequirementPlan.Requirement requirement, Guid responderUserId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(requirement.ResolvedId, out var id)) return;

        var provider = await ResolveProviderInstanceAsync(requirement.ProviderSource, id, cancellationToken).ConfigureAwait(false);

        // Provider instance / repo gone between suspend and respond — can't name a requirement; let the
        // resume proceed (the downstream node surfaces any failure as before).
        if (provider == null) return;

        var identity = await _actorIdentity.ResolveAsync(responderUserId, provider.Value.InstanceId, cancellationToken).ConfigureAwait(false);

        if (identity == null) throw new ActorIdentityRequiredException(provider.Value.Provider, provider.Value.InstanceId);

        // Identity + Active credential confirmed — but that's "who you are", not "can you act on THIS
        // repo". Membership/role is only knowable from the provider, so for a repo-scoped requirement we
        // pre-flight it: a responder who can't contribute is refused HERE (the card stays open, the chat
        // shows why) instead of the write failing later in the background after a misleading "success".
        if (requirement.ProviderSource == ActorProviderSource.Repository)
            await EnsureRepoAccessAsync(id, identity, requirement.CapabilityType, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureRepoAccessAsync(Guid repositoryId, UserProviderIdentity identity, Type? capabilityType, CancellationToken cancellationToken)
    {
        var repo = await _db.Repository.AsNoTracking()
            .Include(r => r.ProviderInstance)
            .SingleOrDefaultAsync(r => r.Id == repositoryId && r.DeletedDate == null, cancellationToken).ConfigureAwait(false);

        // Repo or credential gone (the resolver already gated the credential Active) — degrade to "let the
        // resume proceed". Only a CONCLUSIVE deny throws.
        if (repo == null) { _logger.LogWarning("[preflight] repo {RepoId} not found — skipping repo-access check", repositoryId); return; }

        var credential = await _db.Credential.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == identity.CredentialId && c.DeletedDate == null, cancellationToken).ConfigureAwait(false);

        if (credential == null) { _logger.LogWarning("[preflight] credential {CredId} for the identity not found — skipping repo-access check for {Repo}", identity.CredentialId, repo.FullPath); return; }

        var provider = repo.ProviderInstance.Provider;

        // 1) SCOPE — capability-driven + dynamic, no round-trip. Check the actor's KNOWN token scopes against
        //    the scope THIS action's capability needs, read from the provider module's declared
        //    CapabilityScopeRequirements (the single source of truth). A new act-as-user node's scope check
        //    follows its declared capability — nothing hardcoded. Null capability or unknown scopes → skip.
        if (capabilityType != null && credential.Scopes != null)
        {
            var outcome = _scopeChecker.Check(provider, capabilityType, credential.Scopes);
            if (!outcome.IsSatisfied)
            {
                _logger.LogInformation("[preflight] repo {Repo} ({Provider}): token missing scope(s) [{Missing}] for {Cap}", repo.FullPath, provider, string.Join(", ", outcome.MissingScopes), capabilityType.Name);
                throw new ActorRepoPermissionDeniedException(provider, repo.ProviderInstanceId, repo.FullPath, ScopeReason(provider, outcome.MissingScopes));
            }
        }

        // 2) MEMBERSHIP / repo access — provider-specific (a round-trip). Catches "valid token, but not a
        //    member / role too low". A provider that can't answer → skip (the write stays the backstop).
        if (!_providers.TryGet<IRepositoryAccessCapability>(provider, out var access) || access == null) return;

        var result = await access.GetActorAccessAsync(new ProviderContext(repo.ProviderInstance, credential), ToRemoteRepository(repo), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("[preflight] repo {Repo} ({Provider}) cred {CredId}: canContribute={Can} reason={Reason}", repo.FullPath, provider, credential.Id, result.CanContribute, result.Reason ?? "(none)");

        if (!result.CanContribute)
            throw new ActorRepoPermissionDeniedException(provider, repo.ProviderInstanceId, repo.FullPath, result.Reason);
    }

    private static string ScopeReason(Messages.Enums.ProviderKind provider, IReadOnlyList<string> missing) =>
        $"Your {provider} identity's token is missing the {string.Join(", ", missing)} scope needed for this action. Reconnect it with that scope.";

    // Minimal DB-row → wire-shape projection: the access probe only reads ExternalId, but RemoteRepository's
    // required fields are all present on the row, so we fill them rather than fake them.
    private static RemoteRepository ToRemoteRepository(Repository repo) => new()
    {
        ExternalId = repo.ExternalId,
        NamespacePath = repo.NamespacePath,
        Name = repo.Name,
        FullPath = repo.FullPath,
        DefaultBranch = repo.DefaultBranch,
        Visibility = repo.Visibility,
        WebUrl = repo.WebUrl
    };

    private async Task<(Guid InstanceId, Messages.Enums.ProviderKind Provider)?> ResolveProviderInstanceAsync(ActorProviderSource source, Guid id, CancellationToken cancellationToken)
    {
        if (source == ActorProviderSource.Repository)
        {
            var repo = await _db.Repository.AsNoTracking()
                .Where(r => r.Id == id && r.DeletedDate == null)
                .Select(r => new { r.ProviderInstanceId, r.ProviderInstance.Provider })
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            return repo == null ? null : (repo.ProviderInstanceId, repo.Provider);
        }

        var instance = await _db.ProviderInstance.AsNoTracking()
            .Where(i => i.Id == id)
            .Select(i => new { i.Id, i.Provider })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return instance == null ? null : (instance.Id, instance.Provider);
    }

    // Mirrors WorkflowEngine's run-input scope construction (declared inputs ← trigger payload ← defaults),
    // so {{input.X}} provider refs resolve to the same values the run executes with.
    private static IReadOnlyDictionary<string, JsonElement> BuildInputScope(WorkflowDefinition definition, IReadOnlyDictionary<string, JsonElement> triggerPayload)
    {
        var input = new Dictionary<string, JsonElement>();

        foreach (var declared in definition.Inputs)
        {
            if (triggerPayload.TryGetValue(declared.Name, out var supplied)) input[declared.Name] = supplied;
            else if (declared.Default.HasValue) input[declared.Name] = declared.Default.Value;
        }

        return input;
    }

    private static Dictionary<string, JsonElement> ParsePayloadObject(string? json)
    {
        var dict = new Dictionary<string, JsonElement>();

        if (string.IsNullOrWhiteSpace(json)) return dict;

        var root = JsonDocument.Parse(json).RootElement;
        if (root.ValueKind != JsonValueKind.Object) return dict;

        foreach (var prop in root.EnumerateObject()) dict[prop.Name] = prop.Value.Clone();
        return dict;
    }
}

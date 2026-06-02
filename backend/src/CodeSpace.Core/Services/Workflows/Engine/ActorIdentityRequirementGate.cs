using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Providers.Identity;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Exceptions;
using Microsoft.EntityFrameworkCore;

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

    public ActorIdentityRequirementGate(CodeSpaceDbContext db, INodeRegistry nodeRegistry, IActorIdentityResolver actorIdentity)
    {
        _db = db;
        _nodeRegistry = nodeRegistry;
        _actorIdentity = actorIdentity;
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
    }

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

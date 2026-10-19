using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using MediatR;
using Shouldly;

namespace CodeSpace.UnitTests.Architecture;

/// <summary>
/// Every MediatR request that names a repository, a provider credential or a provider instance by id either declares
/// the access marker that proves the id belongs to the caller's team, or is named here with the reason it does not need
/// one.
///
/// <para>Why pin it: <see cref="IRequireTeamMembership"/> proves the caller belongs to the <c>X-Team-Id</c> team and
/// says nothing about the ids in the body. A request that carries <c>RepositoryId</c> under membership alone reaches
/// its handler with any team's repository id, and the handler loads that repository through ITS owner's credential.
/// That is how agent-pack preview and import read another team's private repository: both carried a
/// <c>RepositoryId</c>, neither declared <see cref="IRequireRepositoryAccess"/>, and the loader filtered on id
/// alone. The marker and the membership marker look identical in a diff; this test makes the difference a build
/// failure.</para>
///
/// <para>Keyed on the property names the markers themselves declare. <c>ProviderInstanceId</c> has no marker, so every
/// request carrying one is named below with the team filter its service applies.</para>
/// </summary>
[Trait("Category", "Unit")]
public class ResourceAccessMarkerInventoryTests
{
    /// <summary>The id property → the marker whose pipeline behavior dereferences it to its owning team. Null = no marker exists.</summary>
    private static readonly IReadOnlyDictionary<string, Type?> MarkerById = new Dictionary<string, Type?>(StringComparer.Ordinal)
    {
        [nameof(IRequireRepositoryAccess.RepositoryId)] = typeof(IRequireRepositoryAccess),
        [nameof(IRequireCredentialAccess.CredentialId)] = typeof(IRequireCredentialAccess),
        ["ProviderInstanceId"] = null,
    };

    /// <summary>
    /// <c>Request.Property</c> → why the marker cannot carry this id (optional, another table, a second id, or no marker
    /// exists) and the team filter that holds instead. Where the marker CAN carry the id, declare it rather than add a
    /// line here; "it is a team request" is never a reason.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ScopedWithoutMarker = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Optional repository ids (Guid?) — the marker's RepositoryId is required, so it cannot carry them.
        ["CompileTaskSpecCommand.RepositoryId"] = "optional; TaskSpecEvidenceReader loads the repository by (id, header team) and a foreign id reads as Unavailable.",
        ["LaunchTaskCommand.RepositoryId"] = "optional; LaunchRepositoryScopeGuard rejects any repository not live in the header team before a session or run is staged.",
        ["PreviewTaskRouteCommand.RepositoryId"] = "optional; TaskRoutePreviewService runs the same LaunchRepositoryScopeGuard before any routing.",
        ["PlanWorkflowFromTaskCommand.RepositoryId"] = "optional; RepoGroundingProvider loads the repository by (id, header team) and a foreign id grounds nothing (PlannerGroundingFlowTests).",
        ["GetSessionRoomFileQuery.RepositoryId"] = "optional disambiguator; RoomFilePreviewService first requires the run to be the header team's and only matches manifests of that run.",

        // Storage credentials share the property name but are a different table the marker does not read.
        ["AppendStorageCredentialRevisionCommand.CredentialId"] = "a storage credential; StorageCredentialService loads it by (header team, id).",
        ["RevokeStorageCredentialCommand.CredentialId"] = "a storage credential; StorageCredentialService loads it by (header team, id).",
        ["GetStorageCredentialQuery.CredentialId"] = "a storage credential; StorageCredentialService loads it by (header team, id).",

        // The credential marker refuses a soft-deleted credential, and a departed member's retired token is the one these two exist for.
        ["GetCredentialDependentsQuery.CredentialId"] = "CredentialRelinkService.LoadTeamCredentialAsync loads the credential by (id, header team), soft-deleted or not, and reads it as not found otherwise.",
        ["GetRelinkSuggestionsQuery.CredentialId"] = "the same CredentialRelinkService.LoadTeamCredentialAsync (id, header team) load as GetCredentialDependentsQuery.",

        // Bind names an instance AND a credential; the marker can vouch for one id, the service vouches for both.
        ["BindRepositoryCommand.CredentialId"] = "RepositoryBindingService.LoadCredentialAsync loads the credential by (id, header team).",
        ["BindRepositoryCommand.ProviderInstanceId"] = "RepositoryBindingService.LoadProviderInstanceAsync loads the instance by (id, header team), and the credential must belong to it.",
        ["BindRepositoriesBulkCommand.CredentialId"] = "the same RepositoryBindingService path as BindRepositoryCommand, per identifier.",
        ["BindRepositoriesBulkCommand.ProviderInstanceId"] = "the same RepositoryBindingService path as BindRepositoryCommand, per identifier.",

        // ProviderInstanceId has no marker.
        ["AddCredentialCommand.ProviderInstanceId"] = "CredentialService.AddAsync calls EnsureProviderInstanceInTeamAsync(id, header team) before it stores anything.",
        ["InitCredentialOAuthCommand.ProviderInstanceId"] = "OAuthFlowService.LoadOwnedInstanceAsync refuses an instance whose team is not the header team.",
        ["LinkProviderIdentityByPatCommand.ProviderInstanceId"] = "UserProviderIdentityService.LoadInstanceAsync loads the instance by (id, header team).",
        ["UpdateProviderInstanceCommand.ProviderInstanceId"] = "ProviderInstanceService.LoadOwnedInstanceAsync refuses an instance outside the header team.",
        ["DeleteProviderInstanceCommand.ProviderInstanceId"] = "ProviderInstanceService.LoadOwnedInstanceAsync refuses an instance outside the header team.",
        ["GetRelinkCandidatesQuery.ProviderInstanceId"] = "CredentialRelinkService.LoadTeamInstanceAsync loads the instance by (id, header team) and reads another team's as not found; every credential it lists is the header team's.",
        ["GetProviderInstanceUsageQuery.ProviderInstanceId"] = "ProviderInstanceService.GetUsageAsync counts only repositories and credentials of the header team.",
        ["ListRepositoriesQuery.ProviderInstanceId"] = "an optional filter over a list RepositoryService already restricts to the header team.",
        ["ListCredentialsQuery.ProviderInstanceId"] = "an optional filter over a list CredentialService already restricts to the header team.",
    };

    [Fact]
    public void Every_request_carrying_a_resource_id_declares_its_access_marker_or_is_allow_listed()
    {
        var offenders = ResourceIds()
            .Where(id => !id.HasMarker)
            .Where(id => !ScopedWithoutMarker.ContainsKey(id.Key))
            .Select(id => id.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "these requests carry a resource id with nothing proving it belongs to the caller's team. Add the marker " +
            "(IRequireRepositoryAccess for RepositoryId, IRequireCredentialAccess for CredentialId) — or, if the service " +
            "already loads the row by (id, header team), add it to ScopedWithoutMarker naming that filter:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_allow_list_does_not_rot()
    {
        var carried = ResourceIds().ToDictionary(id => id.Key, StringComparer.Ordinal);

        foreach (var key in ScopedWithoutMarker.Keys)
        {
            carried.ContainsKey(key).ShouldBeTrue($"allow-listed '{key}' no longer names a request property — remove it");
            carried[key].HasMarker.ShouldBeFalse($"allow-listed '{key}' now declares its access marker — remove it from the allow-list");
        }
    }

    [Fact]
    public void Agent_pack_preview_and_import_prove_repository_access()
    {
        // The two requests the tenancy audit caught reading another team's private repository. Pinned by name so a
        // later "simplification" back to membership-only reds here with the reason, not in a security review.
        typeof(IRequireRepositoryAccess).IsAssignableFrom(typeof(CodeSpace.Messages.Queries.Agents.PreviewAgentPackQuery)).ShouldBeTrue();
        typeof(IRequireRepositoryAccess).IsAssignableFrom(typeof(CodeSpace.Messages.Commands.Agents.ImportAgentPackCommand)).ShouldBeTrue();
    }

    private sealed record ResourceId(string Key, bool HasMarker);

    private static IEnumerable<ResourceId> ResourceIds()
    {
        var found = RequestTypes()
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => MarkerById.ContainsKey(p.Name) && IsGuid(p.PropertyType))
                .Select(p => new ResourceId($"{type.Name}.{p.Name}", MarkerById[p.Name] is { } marker && marker.IsAssignableFrom(type))))
            .ToList();

        found.ShouldContain(id => id.HasMarker, "no request declares a resource marker — the scan is not seeing the requests");

        return found;
    }

    private static bool IsGuid(Type type) => type == typeof(Guid) || type == typeof(Guid?);

    private static IEnumerable<Type> RequestTypes()
    {
        var types = typeof(TeamPermissions).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => typeof(IBaseRequest).IsAssignableFrom(t))
            .ToList();

        types.ShouldNotBeEmpty("the reflection scan found no requests — every check in this class would pass vacuously");

        return types;
    }
}

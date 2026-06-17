using System.Text.Json;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// The SHARED multi-repo workspace AUTHORING底層 (resolver loop #379, S7-A0): the one place that turns an authored
/// <c>relatedRepositories</c> JSON array into <see cref="WorkspaceRepositorySpec"/>s and resolves the canonical
/// <see cref="WorkspaceSpec"/>. EVERY producer of a multi-repo <see cref="AgentTask"/> funnels through here — the
/// <c>agent.code</c> node (from its node inputs) AND the supervisor's spawn (from its agent profile) — so the
/// authored-repos → workspace projection has ONE implementation, never a per-producer hand-mirror (Rule 7, the
/// same "recognise it in ONE place" discipline as <c>SupervisorDecisionKinds.StagesAgents</c>).
///
/// <para>The deeper projection (alias uniqueness, primary defaulting, the single-repo cwd invariant) already lives
/// once in <see cref="WorkspaceSpec.FromAuthoredRepos"/>; this is its authoring-side companion — the JSON parse +
/// the "no related repos → null workspace → byte-identical single-repo run" rule. Pure + deterministic; the producer
/// owns its own source-specific validation (e.g. fail-loud on related-without-primary) and the rest of the task
/// envelope, which legitimately differs per producer.</para>
/// </summary>
public static class AgentWorkspaceAuthoring
{
    /// <summary>
    /// Parse an authored <c>relatedRepositories</c> JSON value — an array of <c>{repositoryId, alias?, access?}</c> —
    /// into related <see cref="WorkspaceRepositorySpec"/>s (access defaults to read-only context; alias defaults to
    /// blank so <see cref="WorkspaceSpec.FromAuthoredRepos"/> assigns a unique one). A non-array value, or a
    /// malformed / idless entry, contributes nothing (lenient — the editor validates the authored shape). Absent /
    /// empty → no related repos → a single-repo run.
    /// </summary>
    public static IReadOnlyList<WorkspaceRepositorySpec> ParseRelatedRepositories(JsonElement relatedArray)
    {
        if (relatedArray.ValueKind != JsonValueKind.Array) return Array.Empty<WorkspaceRepositorySpec>();

        var list = new List<WorkspaceRepositorySpec>();

        foreach (var element in relatedArray.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            if (!element.TryGetProperty("repositoryId", out var idEl) || idEl.ValueKind != JsonValueKind.String || !Guid.TryParse(idEl.GetString(), out var repoId)) continue;

            var alias = element.TryGetProperty("alias", out var aliasEl) && aliasEl.ValueKind == JsonValueKind.String ? (aliasEl.GetString() ?? "").Trim() : "";
            var access = element.TryGetProperty("access", out var accessEl) && accessEl.ValueKind == JsonValueKind.String && string.Equals(accessEl.GetString(), "write", StringComparison.OrdinalIgnoreCase)
                ? WorkspaceAccess.Write
                : WorkspaceAccess.Read;

            list.Add(new WorkspaceRepositorySpec { Alias = alias, RepositoryId = repoId, Access = access });
        }

        return list;
    }

    /// <summary>
    /// Resolve the authored <see cref="WorkspaceSpec"/> a producer attaches to its <see cref="AgentTask"/>: the
    /// multi-repo workspace anchored on <paramref name="primaryRepositoryId"/> when there are related repos, else
    /// NULL — so a no-related-repos run keeps <c>Workspace</c> null and the executor derives the single-repo
    /// workspace from <c>AgentTask.RepositoryId</c> → BYTE-IDENTICAL single-repo execution.
    /// (<see cref="WorkspaceSpec.FromAuthoredRepos"/> already returns null for an empty related list; this also
    /// guards a null primary, the analysis-only case.) <paramref name="primaryRef"/> is the primary's authored ref
    /// (null = the repo default).
    /// </summary>
    public static WorkspaceSpec? ResolveAuthoredWorkspace(Guid? primaryRepositoryId, IReadOnlyList<WorkspaceRepositorySpec> relatedRepositories, string? primaryRef = null) =>
        primaryRepositoryId is { } primaryId ? WorkspaceSpec.FromAuthoredRepos(primaryId, primaryRef, relatedRepositories) : null;
}

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
            var access = ParseAccess(element.TryGetProperty("access", out var accessEl) && accessEl.ValueKind == JsonValueKind.String ? accessEl.GetString() : null);
            var @ref = element.TryGetProperty("ref", out var refEl) && refEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(refEl.GetString()) ? refEl.GetString() : null;
            // A session-inherited related ref carries refSoftFallback:true (set by SerializeRelatedRepositories); an
            // authored related repo never does ⇒ stays HARD (fail loud if its ref is gone).
            var refSoftFallback = element.TryGetProperty("refSoftFallback", out var softEl) && softEl.ValueKind == JsonValueKind.True;
            // S1: the immutable-base pin round-trips too — dropping it here would be the silent tip fallback the pin forbids.
            var pinnedSha = element.TryGetProperty("pinnedSha", out var pinEl) && pinEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pinEl.GetString()) ? pinEl.GetString() : null;

            list.Add(new WorkspaceRepositorySpec { Alias = alias, RepositoryId = repoId, Access = access, Ref = @ref, RefSoftFallback = refSoftFallback, PinnedSha = pinnedSha });
        }

        return list;
    }

    /// <summary>
    /// Project a TYPED authored related repo (the task-launch surface's <c>{repositoryId, alias?, access?}</c>) onto a
    /// <see cref="WorkspaceRepositorySpec"/> with the SAME defaults as the JSON <see cref="ParseRelatedRepositories"/>
    /// path (alias trimmed, blank-OK so <see cref="WorkspaceSpec.FromAuthoredRepos"/> assigns a unique one; access
    /// defaults to read-only context) — so a launch-authored repo and a node-authored one resolve identically (Rule 7).
    /// </summary>
    public static WorkspaceRepositorySpec ToRelatedSpec(Guid repositoryId, string? alias, string? access) =>
        new() { Alias = (alias ?? "").Trim(), RepositoryId = repositoryId, Access = ParseAccess(access) };

    /// <summary>
    /// Serialize related specs back to the authored <c>{repositoryId, alias?, access?, ref?}</c> JSON shape both the
    /// <c>agent.code</c> node input AND the supervisor config consume — the inverse of <see cref="ParseRelatedRepositories"/>,
    /// in ONE place (Rule 7) so a projection emits the EXACT shape these re-parse. Null / empty ⇒ null, so a caller
    /// omits the key entirely (a single-repo projection stays byte-identical). Alias is omitted when blank (the
    /// workspace re-derives a unique one).
    /// <para><paramref name="baseRefs"/> (session branch continuity) supplies a per-repo clone ref: when a repo has an
    /// entry, its <c>ref</c> is emitted so the agent clones it at the prior turn's produced branch. Null map / a repo
    /// absent from it ⇒ NO <c>ref</c> key for that entry (byte-identical — the repo clones at its default branch). The
    /// agent.code path passes the map; the supervisor passes null (it has no per-repo continuity — out of scope).</para>
    /// </summary>
    public static IReadOnlyList<Dictionary<string, object?>>? SerializeRelatedRepositories(IReadOnlyList<WorkspaceRepositorySpec>? related, IReadOnlyDictionary<Guid, string>? baseRefs = null)
    {
        if (related is not { Count: > 0 }) return null;

        return related.Select(r =>
        {
            var entry = new Dictionary<string, object?>
            {
                ["repositoryId"] = r.RepositoryId.ToString(),
                ["alias"] = string.IsNullOrWhiteSpace(r.Alias) ? null : r.Alias,
                ["access"] = r.Access == WorkspaceAccess.Write ? "write" : "read",
            };

            if (baseRefs is not null && baseRefs.TryGetValue(r.RepositoryId, out var br) && !string.IsNullOrWhiteSpace(br))
            {
                entry["ref"] = br;
                // The related ref came from the SESSION base-refs map (a transient prior produced branch) → mark it
                // SOFT so the clone falls back to the default branch if it was pruned (parity with the primary baseRef).
                entry["refSoftFallback"] = true;
            }

            // S1: the pin survives the projection round-trip (omitted when null — byte-identical to before).
            if (!string.IsNullOrWhiteSpace(r.PinnedSha)) entry["pinnedSha"] = r.PinnedSha;

            return entry;
        }).ToList();
    }

    /// <summary>The authored access string → enum: <c>"write"</c> (case-insensitive) ⇒ writable, anything else (incl. null/blank/garbage) ⇒ read-only context — the safe default. The ONE place the access wire-value is interpreted (shared by the JSON + typed authoring paths).</summary>
    internal static WorkspaceAccess ParseAccess(string? access) =>
        string.Equals(access, "write", StringComparison.OrdinalIgnoreCase) ? WorkspaceAccess.Write : WorkspaceAccess.Read;

    /// <summary>
    /// Resolve the authored <see cref="WorkspaceSpec"/> a producer attaches to its <see cref="AgentTask"/>: the
    /// multi-repo workspace anchored on <paramref name="primaryRepositoryId"/> when there are related repos, else
    /// NULL — so a no-related-repos run keeps <c>Workspace</c> null and the executor derives the single-repo
    /// workspace from <c>AgentTask.RepositoryId</c> → BYTE-IDENTICAL single-repo execution.
    /// (<see cref="WorkspaceSpec.FromAuthoredRepos"/> already returns null for an empty related list; this also
    /// guards a null primary, the analysis-only case.) <paramref name="primaryRef"/> is the primary's authored ref
    /// (null = the repo default).
    /// <para>EXCEPTION: a single-repo run that PINS a primary ref (session branch continuity — start the next turn
    /// from the prior turn's produced branch) needs an EXPLICIT one-repo spec so the resolver clones at that ref;
    /// without a ref it stays null (byte-identical — the executor derives <c>FromRepository(id)</c> at the default
    /// branch). So: related repos ⇒ the multi-repo spec; else a pinned ref ⇒ <c>FromRepository(id, ref)</c>; else null.</para>
    /// </summary>
    public static WorkspaceSpec? ResolveAuthoredWorkspace(Guid? primaryRepositoryId, IReadOnlyList<WorkspaceRepositorySpec> relatedRepositories, string? primaryRef = null, bool primaryRefSoftFallback = false, WorkspaceCwdMode cwdMode = WorkspaceCwdMode.Auto)
    {
        if (primaryRepositoryId is not { } primaryId) return null;

        // cwdMode only bites a MULTI-repo workspace; a single-repo run always runs at the repo root (the single-repo
        // invariant), so the pinned-ref single-repo branch below ignores it (byte-identical).
        if (relatedRepositories.Count > 0) return WorkspaceSpec.FromAuthoredRepos(primaryId, primaryRef, relatedRepositories, primaryRefSoftFallback, cwdMode);

        return string.IsNullOrWhiteSpace(primaryRef) ? null : WorkspaceSpec.FromRepository(primaryId, primaryRef, primaryRefSoftFallback);
    }
}

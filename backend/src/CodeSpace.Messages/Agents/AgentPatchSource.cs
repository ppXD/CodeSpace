namespace CodeSpace.Messages.Agents;

/// <summary>
/// WHERE one producer's captured diff actually lives (a pure data noun, Rule 18.1) — the coordinates
/// <c>IAgentPatchReader</c> resolves into patch bytes. Patch offload is SIZE-gated, so the two carriers are
/// mutually exclusive by construction: a diff over the artifact store's inline threshold lives in
/// <see cref="PatchArtifactId"/> and nowhere else, while a diff at or below it is never offloaded and lives ONLY
/// inline in the producing agent run's recorded result. A publish manifest row records the first carrier and not
/// the second, which is why a reader that consults only the manifest sees every small diff as no diff at all.
/// </summary>
public sealed record AgentPatchSource
{
    /// <summary>The producing agent run whose recorded result carries the INLINE diff. Null ⇒ there is no inline carrier to read (nothing produced it, or the caller has only an artifact ref).</summary>
    public Guid? AgentRunId { get; init; }

    /// <summary>The workspace alias this diff belongs to ("primary" for a single-repo run) — the manifest row's own alias, which selects the matching per-repo entry of a multi-repo result.</summary>
    public string RepositoryAlias { get; init; } = "primary";

    /// <summary>The artifact-store id holding the FULL diff when it exceeded the inline threshold. Non-null WINS: the artifact is the whole diff, and the result row's inline field was cleared when it was offloaded.</summary>
    public Guid? PatchArtifactId { get; init; }
}

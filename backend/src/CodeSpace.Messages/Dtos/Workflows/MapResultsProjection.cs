using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// What <c>MapResultsPrompt</c> returns for one <c>flow.map</c>: the prompt-ready text a downstream reduce binds,
/// PAIRED with the <see cref="MapResultsCoverage"/> that text actually achieved.
///
/// <para>The pairing is the point. A bounded projection can only be honest about being partial if the partiality
/// travels with it as data — a caveat inside <see cref="Text"/> is addressed to a model, and whether the model
/// honors it is unverifiable, so a run that synthesized 3 of 20 branches would otherwise be indistinguishable in
/// the data from one that synthesized 20 of 20. The coverage is written by the code that DID the cutting, never by
/// a model, and it is what the engine persists beside the text.</para>
/// </summary>
public sealed record MapResultsProjection
{
    /// <summary>The projected results as a string within the caller's character budget — what a reduce prompt binds.</summary>
    public required string Text { get; init; }

    /// <summary>How much of the results <see cref="Text"/> carries. Persisted as its own map output so it is readable without re-deriving it from the text.</summary>
    public required MapResultsCoverage Coverage { get; init; }
}

/// <summary>
/// How much of a <c>flow.map</c>'s reduced results a bounded projection carries — the STRUCTURAL record of
/// partiality that the engine writes into the map node's output bag under
/// <c>WorkflowOutputKeys.MapResultsCoverage</c>.
///
/// <para>The wire names are pinned with <see cref="JsonPropertyNameAttribute"/> because they ARE the binding path a
/// downstream node reads (<c>{{nodes.&lt;map&gt;.outputs.resultsCoverage.complete}}</c>) and the shape the
/// run-detail projection hands the UI. Serializing this record IS the persisted shape — there is no hand-built
/// second copy that could drift from it.</para>
/// </summary>
public sealed record MapResultsCoverage
{
    /// <summary>False whenever ANY content was dropped — a branch left out entirely, or a branch shortened. The single flag a downstream consumer gates on to know the projection is not the whole input.</summary>
    [JsonPropertyName("complete")]
    public required bool Complete { get; init; }

    /// <summary>How many branches the map reduced — the denominator.</summary>
    [JsonPropertyName("totalBranches")]
    public required int TotalBranches { get; init; }

    /// <summary>How many of those branches appear in the projection at all. Below <see cref="TotalBranches"/> means the remainder is absent, not merely shortened.</summary>
    [JsonPropertyName("includedBranches")]
    public required int IncludedBranches { get; init; }

    /// <summary>The element indices of INCLUDED branches whose own text had to be cut to fit. Empty when every included branch is present in full.</summary>
    [JsonPropertyName("shortenedBranches")]
    public IReadOnlyList<int> ShortenedBranches { get; init; } = Array.Empty<int>();
}

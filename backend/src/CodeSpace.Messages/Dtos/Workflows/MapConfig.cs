namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// Static configuration of a <c>flow.map</c> fan-out container node. The body subgraph (nodes whose
/// <c>ParentId</c> is this node, rooted at a <c>flow.map_start</c>) runs once per element of the bound
/// collection (the node's <c>items</c> INPUT, not config — it resolves at runtime from a
/// <c>{{...}}</c> reference). The N element-branches run as a bounded-parallel batch; each branch sees
/// its element as <c>{{item}}</c> / <c>{{index}}</c> and its terminal node's output becomes that
/// element's result. The per-element results reduce into a keyed array (<see cref="ResultKey"/>,
/// default <c>"results"</c>) a downstream synthesizer reads as <c>{{nodes.&lt;map&gt;.outputs.results}}</c>.
///
/// <para>Parsed from the node's raw <c>Config</c> JSON. Unlike <c>LoopConfig</c> there are no
/// loop-variable / termination refs to re-resolve, so the engine reads it once per map execution.</para>
/// </summary>
public sealed record MapConfig
{
    /// <summary>
    /// Optional cap on how many element-branches run concurrently. Null (default) inherits the
    /// engine-wide setting (the <c>CODESPACE_WORKFLOW_MAX_PARALLELISM</c> env value); set it to throttle
    /// a body that hits a rate-limited resource (e.g. <c>1</c> = strictly sequential branches). The
    /// engine clamps to <c>[1, 64]</c>. Absent from existing configs ⇒ no behaviour or hash change.
    /// </summary>
    public int? MaxParallelism { get; init; }

    /// <summary>
    /// What the map does when an element-branch fails and the failing node has no <c>error</c> edge of
    /// its own. Parsed leniently (unknown/empty ⇒ <see cref="MapErrorHandling.Terminate"/>, the safe
    /// default). Values: <c>"terminate"</c> (a failed branch fails the whole map) | <c>"continue"</c>
    /// (that element's result is a failure marker, the map's <c>failed</c> count increments, and the
    /// map proceeds). A node that DOES have its own error edge is routed there first, regardless.
    /// </summary>
    public string? ErrorHandling { get; init; }

    /// <summary>
    /// The output key the reduced array lands under — <c>scope.Nodes[mapId][resultKey]</c>, read
    /// downstream as <c>{{nodes.&lt;map&gt;.outputs.&lt;resultKey&gt;}}</c>. Defaults to <c>"results"</c>.
    /// A blank/whitespace value falls back to the default at parse time.
    /// </summary>
    public string ResultKey { get; init; } = "results";

    /// <summary>
    /// Optional ceiling, in USD, on what this fan-out may spend. Null (default) means unbounded, which is what
    /// every map did before this existed — an absent key leaves the config byte-identical, so no stored definition
    /// changes hash. Set, the engine admits each branch against the run's shared budget ledger before dispatching
    /// it and refuses the branches that would cross the ceiling.
    /// </summary>
    public decimal? MaxCostUsd { get; init; }

    /// <summary>
    /// Optional CHARACTER budget for a prompt-ready projection of the reduced array. Null (default) — every map
    /// before this existed, and every map that does not feed a model — emits no projection at all, leaving the
    /// output bag byte-identical. Set, the map ALSO emits
    /// <c>WorkflowOutputKeys.MapResultsPrompt</c>: the results rendered as a string that fits the budget, with an
    /// excerpt notice and per-branch markers whenever content had to be dropped (<c>MapResultsPrompt</c>). A reduce
    /// prompt binds THAT instead of the raw array so it cannot outgrow the model's context window; the raw array
    /// under <see cref="ResultKey"/> is untouched and stays the structured view for everything else.
    /// The engine raises a set-but-tiny value to <c>MapPlan.MinPromptBudgetChars</c> so a projection always has room
    /// for its notice plus at least one readable branch slice.
    /// </summary>
    public int? PromptBudgetChars { get; init; }
}

/// <summary>
/// How a <c>flow.map</c> reacts to an unhandled element-branch failure (a branch node that fails and
/// has no <c>error</c> edge). <see cref="Terminate"/> fails the whole map (its own error edge may still
/// catch it); <see cref="Continue"/> records that element's result as a failure marker, increments the
/// map's <c>failed</c> count, and keeps fanning the rest — so one bad element doesn't sink the batch.
/// </summary>
public enum MapErrorHandling { Terminate, Continue }

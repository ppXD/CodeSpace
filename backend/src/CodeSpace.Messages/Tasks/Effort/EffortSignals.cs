namespace CodeSpace.Messages.Tasks.Effort;

/// <summary>
/// The task-TYPE-AGNOSTIC properties an effort classifier extracts from a task, over which <c>EffortPolicy</c>
/// decides a tier (Rule 18.1, a pure data noun). Deliberately NOT "is this a refactor / a bugfix?" — there is
/// no <c>TaskKind</c> field and no per-task-type branch anywhere. Each property is a generic, observable
/// PROPERTY of the work (does it change code? across files? touch tests? risky side effects? how big?), so the
/// policy is a small ordered rule table over these axes rather than a switch on a closed task taxonomy.
///
/// <para>A new signal is an ADDITIVE optional field (defaulting to the cheap / safe value) plus a new policy row
/// — never a widening of an enum or a rewrite of existing rows (Rule 7). Every bool defaults <c>false</c> (the
/// cheap reading); <see cref="EstimatedCostTier"/> defaults <c>"low"</c>.</para>
/// </summary>
public sealed record EffortSignals
{
    /// <summary>The task likely edits code (vs a pure read / analysis / question). False = no code change expected.</summary>
    public bool NeedsCodeChange { get; init; }

    /// <summary>The work likely spans multiple files / modules rather than a single localized edit.</summary>
    public bool CrossFile { get; init; }

    /// <summary>The task likely needs tests written / run or CI to pass before it is done.</summary>
    public bool NeedsTestsOrCi { get; init; }

    /// <summary>The goal is under-specified / open-ended enough that a plan-review or a confirm is warranted.</summary>
    public bool Ambiguous { get; init; }

    /// <summary>The task may produce risky / irreversible side effects (delete, drop, migrate, deploy, production, secrets).</summary>
    public bool RiskySideEffects { get; init; }

    /// <summary>A rough cost / complexity tier as an open string (e.g. <c>"low"</c>, <c>"medium"</c>, <c>"high"</c>). Defaults to the cheapest reading.</summary>
    public string EstimatedCostTier { get; init; } = "low";
}

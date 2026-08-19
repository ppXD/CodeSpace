namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Arc D / D1 — one distilled cross-run lesson: what failed, why, and how to apply it next time (the Reflexion
/// three-part shape), CITING the runs that taught it. A lesson without citations cannot exist — provenance is the
/// anti-confabulation guard. Consolidation may UPDATE a lesson (merging citations) and INVALIDATE it one-way
/// (temporal, Graphiti-style); readers see only current rows and history is never rewritten.
/// </summary>
public class Lesson : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid TeamId { get; set; }

    /// <summary>The operating mode the cited runs ran under (a <c>RunModeKeys</c> value) — D2's retrieval key.</summary>
    public string Mode { get; set; } = "";

    /// <summary>The one repository every cited run scoped to — null when the citations span repos (or none).</summary>
    public Guid? RepositoryId { get; set; }

    /// <summary>The model-inferred failure class (open string, e.g. "broken-acceptance-command") — a post-mortem taxonomy, deliberately not the live remedy enum.</summary>
    public string FailureClass { get; set; } = "";

    public string WhatFailed { get; set; } = "";

    public string Why { get; set; } = "";

    public string HowToApply { get; set; } = "";

    /// <summary>The runs that taught this lesson — never empty, only ids the distiller actually showed the model.</summary>
    public List<Guid> SourceRunIds { get; set; } = [];

    /// <summary>Which model wrote the lesson — the distillation is only as good as the brain that did it.</summary>
    public string DistilledByModel { get; set; } = "";

    public DateTimeOffset ValidFrom { get; set; }

    /// <summary>One-way temporal invalidation — set once when consolidation retires the lesson; never cleared.</summary>
    public DateTimeOffset? InvalidatedAt { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}

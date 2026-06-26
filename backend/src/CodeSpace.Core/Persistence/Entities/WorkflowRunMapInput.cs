namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Frozen snapshot of a <c>flow.map</c>'s RESOLVED collection — the element array a map fanned out over,
/// captured ATOMICALLY at the map's FIRST fan-out and read by every later re-entry (same-run suspend/resume)
/// INSTEAD of re-resolving the <c>items</c> binding. The per-map analogue of <see cref="WorkflowRunVariable"/>:
/// it pushes the determinism boundary up to the INPUT array.
///
/// <para>Why it exists: <c>ExecuteMapAsync</c> re-resolves <c>items</c> from the live scope on EVERY entry. A
/// branch that suspends and resumes after an upstream output changed would re-resolve a DIFFERENT array
/// (length / order / content) — silently shifting branch indices and corrupting the index-keyed branch replay
/// and the ordered reduce. Loading this snapshot keeps the array byte-stable across re-entries.</para>
///
/// <para>Storage rules (the value-column CHECK constraint enforces the secret rule):
///   • <c>Sensitivity = "SecretDerived"</c> (the map's <c>items</c> binding references a secret path) →
///     <c>ElementsJson</c> is NULL; the array is re-resolved live on read, mirroring
///     <see cref="WorkflowRunVariable"/>'s secret rule — no plaintext secret is ever frozen at rest, and the
///     map's resume behaviour is unchanged from a pre-snapshot run (no regression).
///   • <c>Sensitivity = "Plain"</c> → <c>ElementsJson</c> holds the frozen JSON array verbatim (a large array
///     is offloaded via the artifact-ref envelope, resolved on read), frozen forever.
///   • <c>ElementCount</c> is ALWAYS the true resolved length — it distinguishes a genuinely empty array from
///     a transient resolve-to-null on resume (which must NOT zero an already-fanned-out map).
/// </para>
///
/// <para>Keyed by (RunId, MapNodeId, IterationKey) where <c>IterationKey</c> is the map's OWN enclosing-container
/// key (empty at top level, the loop / iterate key when nested) — NOT the per-branch <c>"&lt;mapId&gt;#&lt;i&gt;"</c>
/// key — so a map nested in a loop iteration gets ONE snapshot row per outer pass. The DB unique constraint makes
/// the get-or-create race-safe: a concurrent re-walk or crash-replay loses the insert on 23505 and re-reads the
/// winning row.</para>
/// </summary>
public class WorkflowRunMapInput
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }

    /// <summary>The <c>flow.map</c> node's id.</summary>
    public string MapNodeId { get; set; } = default!;

    /// <summary>The map's OWN enclosing-container iteration key (empty string at top level), NOT the per-branch <c>"&lt;mapId&gt;#&lt;i&gt;"</c> key.</summary>
    public string IterationKey { get; set; } = default!;

    /// <summary>The run's release hash at execution (<see cref="WorkflowRun.ReleaseHashAtRun"/>). Written now; CONSUMED by the
    /// fork/rerun slices as a drift guard (never read a snapshot against a drifted definition). Same-run resume never drifts,
    /// so this slice stores it for the future reader rather than reading it — forensic until then.</summary>
    public string DefinitionHash { get; set; } = default!;

    /// <summary>The true resolved element count — distinguishes a real empty array from a transient resolve-to-null on resume.</summary>
    public int ElementCount { get; set; }

    /// <summary>Frozen JSON of the resolved element array (an offload-ref envelope when large); NULL when <c>Sensitivity = "SecretDerived"</c> (re-resolved live).</summary>
    public string? ElementsJson { get; set; }

    /// <summary>SHA-256 (uppercase hex) over the LOGICAL (pre-offload) resolved array. Written now; CONSUMED by the fork / rerun
    /// slices for sibling-reuse integrity (compared against the re-inflated array, not the raw ElementsJson). Forensic until then.</summary>
    public string ContentHash { get; set; } = default!;

    /// <summary>"Plain" | "SecretDerived" — whether the <c>items</c> binding references a secret path.</summary>
    public string Sensitivity { get; set; } = default!;

    public DateTimeOffset CapturedAt { get; set; }

    public WorkflowRun Run { get; set; } = default!;
}

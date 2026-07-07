using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// The I1 (record) / I3 (complete) invariant lives on this single field: EVERY <c>PublishManifest</c> row for a
/// non-empty diff resolves to exactly one of these — never left implicit by an absent branch. <see cref="Pushed"/>
/// is the only state with a live remote branch; every other state still carries a non-null <c>PatchArtifactId</c>
/// (the work is never merely gone), so a human can always <c>git apply</c> it back regardless of which state landed.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PublishState
{
    /// <summary>No diff to publish (an empty-diff run) — the only state with no patch artifact either.</summary>
    None,

    /// <summary>Pushed to a live remote branch — <c>Branch</c>/<c>CommitSha</c> are set.</summary>
    Pushed,

    /// <summary>A non-empty diff exists (patch artifact set) but no remote branch — by policy choice, a missing
    /// credential, or a push failure after retries. <see cref="PublishError"/> distinguishes an intentional
    /// skip from a failure; either way the patch is recoverable.</summary>
    PatchOnly,
}

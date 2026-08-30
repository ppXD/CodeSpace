using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// The non-secret diagnostic object an <c>artifact_location_event</c> carries when a placement's record is CLOSED.
///
/// <para>Deleting the bytes and abandoning the record converge on <c>Purged</c> through one finalize and one
/// <c>StateChanged</c> event, so without a verb the ledger cannot answer the only question an operator asks
/// afterwards: is anything still out there? <see cref="Deleted"/> means the destination was asked to remove the
/// bytes and did not refuse. <see cref="Abandoned"/> means nothing was deleted — the record was closed on what the
/// destination said, and bytes may still sit at the key.</para>
///
/// <para>Only the VERB was ever missing. The coordinate survives a purge on the row itself, where
/// <c>ck_artifact_location_identity</c> keeps <c>object_key</c> and <c>locator</c> non-blank.</para>
///
/// <para>Anyone who can read the ledger can read this, and what constrains it is a SHAPE rather than a filter: the
/// verb is a literal from this file, and the only other thing reaching the column is the sentence one
/// <see cref="ArtifactLocationAbandonment"/> authored. That fixes which FIELDS get written; it does not look at what
/// arrives inside one, and nothing here scrubs. What the shape over there does and does not buy is stated where that
/// sentence is authored, and this carries the same limit.</para>
/// </summary>
internal static class ArtifactLocationClosureDetails
{
    private const string ClosureKey = "closure";
    private const string ObservedKey = "observed";

    /// <summary>The destination was asked to delete the bytes and did not refuse. It observed nothing else, and claims nothing else.</summary>
    public static string Deleted() => Describe("deleted", null);

    /// <summary>The record was closed without deleting anything, on <paramref name="abandonment"/> — which is the whole of what the operator gets to read back.</summary>
    public static string Abandoned(ArtifactLocationAbandonment abandonment) => Describe("abandoned", abandonment.Observed);

    private static string Describe(string closure, string? observed)
    {
        var details = new Dictionary<string, string> { [ClosureKey] = closure };

        if (observed != null) details[ObservedKey] = observed;

        return JsonSerializer.Serialize(details);
    }
}

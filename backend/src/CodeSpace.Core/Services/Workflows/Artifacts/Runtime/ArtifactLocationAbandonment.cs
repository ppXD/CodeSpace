using CodeSpace.Core.Services.Workflows.Artifacts.Providers;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// What an abandonment observed, in the only two shapes anything is allowed to say it.
///
/// <para>An observation lands in <c>artifact_location_event.details_jsonb</c>, which anyone who can read the database
/// can read, and once written it is durable, replicated and unnoticed. So the sentence is authored HERE rather than
/// handed in. Exactly two fields reach it. One is a provider code — an <see cref="ArtifactStorageErrorCode"/>, a
/// closed enum, so it can be nothing but one of the names declared in it. The other is the placement's object key,
/// which both callers take from <see cref="ArtifactCasPurgeClaim.ObjectKey"/>, copied off the row that
/// <c>ck_artifact_location_identity</c> keeps it non-blank on. A driver's message, the URL it was reached at, a
/// credential broker's complaint: none of them has a parameter to arrive through.</para>
///
/// <para>Be exact about what that buys, because the enforcement is on the SHAPE. It fixes which FIELDS may be
/// written; it cannot say the value arriving in a permitted one is not a secret. Nothing here inspects an object
/// key, so a caller passing something else as one would put that text in the ledger unexamined — the key is safe
/// because every caller reads it off the row, which is a fact about those two call sites and not something this
/// class checks. The shape is the part a test can hold, and <c>ClosureDetailsTests</c> holds it: the authors below
/// take those two parameters and no others, at any accessibility. So an author who adds a field, or hands one of
/// these a value sourced from anywhere but the row, defeats the guarantee — which is the edit to argue about.</para>
/// </summary>
internal sealed class ArtifactLocationAbandonment
{
    private ArtifactLocationAbandonment(string observed) => Observed = observed;

    /// <summary>What the operator reads back — as the abandonment's evidence, and as the ledger entry's observation.</summary>
    public string Observed { get; }

    /// <summary>The destination could not serve the object, and was still answering for ITSELF when it said so.</summary>
    public static ArtifactLocationAbandonment Unservable(ArtifactStorageErrorCode answer, string objectKey) =>
        new($"the destination answered '{answer}' for {objectKey} while still answering for itself");

    /// <summary>The destination is demonstrably holding something that is not this object.</summary>
    public static ArtifactLocationAbandonment HoldsSomethingElse(string objectKey) =>
        new($"the destination holds something other than this object at {objectKey}");
}

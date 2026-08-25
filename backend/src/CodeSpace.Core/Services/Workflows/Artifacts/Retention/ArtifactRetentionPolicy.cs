using CodeSpace.Messages.Artifacts;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Retention;

/// <summary>
/// One retention class's committed rule. <paramref name="MinimumAge"/> is the age floor measured from the artifact's
/// own <c>created_at</c>: below it the artifact is not even considered, so a just-written object whose reference is
/// still in flight cannot be reached. <paramref name="QuarantineWindow"/> is the second, independent wait measured
/// from the first observation of "unreferenced" — collection needs BOTH to have elapsed.
/// </summary>
public sealed record ArtifactRetentionRule(ArtifactRetentionClass Class, TimeSpan MinimumAge, TimeSpan QuarantineWindow);

/// <summary>
/// The retention policy: which classes exist and how long their objects are kept. Values are committed here and
/// changed by a pull request — there is no environment override, because a mistyped retention window is unrecoverable
/// data loss and a code review is the control that belongs in front of it.
///
/// <para>An unregistered class is NOT an error the reaper can shrug off: <see cref="For"/> returns null and the reaper
/// settles the row <see cref="ArtifactRetentionState.Indeterminate"/>, which keeps the artifact forever. That is what
/// makes removing a class from this table a safe operation.</para>
/// </summary>
public static class ArtifactRetentionPolicy
{
    /// <summary>
    /// Captured deliverable bytes behind an <c>artifact_manifest</c> row. Seven days is deliberately far longer than
    /// any run: the objects this class actually reclaims are the ones whose manifest row never landed at all, so the
    /// floor costs nothing and buys a wide margin against a producer that is slower than expected.
    /// </summary>
    public static readonly ArtifactRetentionRule ArtifactManifestContent =
        new(ArtifactRetentionClass.ArtifactManifestContent, TimeSpan.FromDays(7), TimeSpan.FromHours(24));

    public static readonly ArtifactRetentionRule SensitiveRecordPayload =
        new(ArtifactRetentionClass.SensitiveRecordPayload, TimeSpan.FromDays(7), TimeSpan.FromHours(24));

    private static readonly IReadOnlyDictionary<ArtifactRetentionClass, ArtifactRetentionRule> Rules =
        new Dictionary<ArtifactRetentionClass, ArtifactRetentionRule>
        {
            [ArtifactManifestContent.Class] = ArtifactManifestContent,
            [SensitiveRecordPayload.Class] = SensitiveRecordPayload,
        };

    /// <summary>The rule for <paramref name="value"/>, or null when the running policy does not register it — which the reaper reads as "cannot tell" and keeps.</summary>
    /// <summary>The rule for a class NAME, or null when this build registers none — including a name a rolled-back build wrote that this one has never heard of. Null settles as keep.</summary>
    public static ArtifactRetentionRule? For(string value) => Enum.TryParse<ArtifactRetentionClass>(value, ignoreCase: false, out var parsed) && Rules.TryGetValue(parsed, out var rule) ? rule : null;

    /// <summary>
    /// The smallest age floor across every registered class. The claim query uses it as a cheap SQL pre-filter; the
    /// exact per-class floor is still enforced per row, so a class with a longer floor is never shortened by this.
    /// </summary>
    public static TimeSpan MinimumAgeFloor => Rules.Values.Min(rule => rule.MinimumAge);
}

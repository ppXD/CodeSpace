using CodeSpace.Messages.Dtos.Storage;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// The precondition both the survey and each bounded phase-two command must clear before phase two may write a single
/// <c>artifact_location</c> row for the pre-CAS tier.
/// </summary>
public static class LegacyAdoptionRules
{
    /// <summary>
    /// Whether a minting pass may run for the profile a survey with these numbers describes: only when the pass ran,
    /// the layout NAMED rows, and the destination ANSWERED for some of them. Both halves, or the profile waits.
    ///
    /// <para>Resolution alone is not enough. It proves only the key mapping — that this layout reproduces the locator
    /// each row already carries — and a profile whose keys all resolve against a root that is unmounted or emptied
    /// confirms nothing while resolving everything. Admitting that would mint a row per artifact naming a key that
    /// holds nothing, and every monitoring component in the plane would then report a healthy destination as gutted
    /// on the strength of a mount that was not there for one pass.</para>
    ///
    /// <para>Confirmation alone is not enough either. It proves only that SOMETHING answers at a key, never that the
    /// something is the object the row names: a layout free to ask about keys it had not first checked against each
    /// row's own locator could confirm a different tier's objects sitting at the same paths, and mint rows pointing
    /// this team's artifacts at bytes that were never theirs.</para>
    /// </summary>
    public static bool AdmitsAdoption(LegacyPlacementSurveyRefusalValue refusal, int resolved, int confirmed) =>
        refusal == LegacyPlacementSurveyRefusalValue.None && resolved > 0 && confirmed > 0;
}

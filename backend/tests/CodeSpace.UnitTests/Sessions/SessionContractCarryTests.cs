using CodeSpace.Core.Services.Sessions;
using CodeSpace.Messages.Contracts;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.UnitTests.Sessions;

/// <summary>
/// 🟢 Unit: pins P4-U3 (L5 contract carry) — the ONE line that surfaces a prior turn's unresolved contract to the
/// continuing planner. A clean turn renders NOTHING (no noise); an unclean one names exactly the dimensions still
/// owed plus the would-be terminal; a malformed record renders nothing rather than a wrong claim.
/// </summary>
[Trait("Category", "Unit")]
public class SessionContractCarryTests
{
    private static string Json(OutcomeDisposition outcome = OutcomeDisposition.Solved, VerificationDisposition verification = VerificationDisposition.Passed, ArtifactDisposition artifact = ArtifactDisposition.Captured, DeliveryDisposition delivery = DeliveryDisposition.Delivered) =>
        JsonSerializer.Serialize(new CompletionAssessment
        {
            Basis = CompletionBasis.ContractDerived,
            Execution = ExecutionDisposition.Completed,
            Outcome = outcome, Verification = verification, Artifact = artifact, Delivery = delivery,
        }, CodeSpace.Core.Services.Agents.AgentJson.Options);

    [Fact]
    public void A_clean_turn_renders_nothing()
    {
        SessionContextBuilder.RenderCompletion(Json(), "CleanSuccess").ShouldBeNull("no noise on a settled turn");
        SessionContextBuilder.RenderCompletion(Json(verification: VerificationDisposition.NotApplicable, artifact: ArtifactDisposition.NothingExpected, delivery: DeliveryDisposition.NotRequired), null)
            .ShouldBeNull("authorized-NA conjuncts are settled, not concerns");
    }

    [Fact]
    public void An_unclean_turn_names_exactly_what_is_owed()
    {
        var line = SessionContextBuilder.RenderCompletion(Json(outcome: OutcomeDisposition.Unsolved, verification: VerificationDisposition.Failed, delivery: DeliveryDisposition.PolicyBlocked), "HonestFailure");

        line.ShouldNotBeNull();
        line!.ShouldContain("UNRESOLVED CONTRACT");
        line.ShouldContain("outcome=Unsolved");
        line.ShouldContain("verification=Failed");
        line.ShouldContain("delivery=PolicyBlocked");
        line.ShouldNotContain("artifact=", customMessage: "settled dimensions are omitted — the line names only what is owed");
        line.ShouldContain("would-be terminal: HonestFailure");
    }

    [Fact]
    public void A_malformed_record_renders_nothing_never_a_wrong_claim()
    {
        SessionContextBuilder.RenderCompletion("{not json", "Park").ShouldBeNull();
    }
}

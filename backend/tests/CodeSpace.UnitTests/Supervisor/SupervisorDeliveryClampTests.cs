using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Supervisor;

/// <summary>
/// DC-1 — <see cref="SupervisorDeliveryClamp.Clamp"/>, pure over the model's proposal + the operator's own
/// pre-declared preference. The one invariant every case must prove: the operator's OWN declared value always
/// wins PER FIELD when set, including an explicit <c>false</c> surviving a model proposing the opposite.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorDeliveryClampTests
{
    [Fact]
    public void No_operator_contract_leaves_the_models_proposal_untouched()
    {
        var proposed = new DeliverySpec { OpenPullRequest = true, TargetBranch = "develop" };

        SupervisorDeliveryClamp.Clamp(proposed, operatorDeclared: null).ShouldBe(proposed);
    }

    [Fact]
    public void No_model_proposal_uses_the_operators_contract_verbatim()
    {
        var declared = new DeliverySpec { OpenPullRequest = true, TargetBranch = "main" };

        SupervisorDeliveryClamp.Clamp(modelProposed: null, declared).ShouldBe(declared);
    }

    [Fact]
    public void Neither_side_names_anything_clamps_to_null_not_an_all_null_placeholder()
    {
        SupervisorDeliveryClamp.Clamp(modelProposed: null, operatorDeclared: null).ShouldBeNull();
        SupervisorDeliveryClamp.Clamp(new DeliverySpec(), new DeliverySpec()).ShouldBeNull("both objects exist but every field is null on both — the clamp must still collapse to no contract");
    }

    [Fact]
    public void An_operator_declared_false_overrides_a_model_proposing_true()
    {
        // DC-1's whole point: a user's "don't open a PR" is never overridable by the model.
        var proposed = new DeliverySpec { OpenPullRequest = true };
        var declared = new DeliverySpec { OpenPullRequest = false };

        var clamped = SupervisorDeliveryClamp.Clamp(proposed, declared);

        clamped!.OpenPullRequest.ShouldBe(false, "the operator's explicit false must survive against the model's proposed true");
    }

    [Fact]
    public void An_operator_declared_true_overrides_a_model_proposing_false()
    {
        var proposed = new DeliverySpec { OpenPullRequest = false };
        var declared = new DeliverySpec { OpenPullRequest = true };

        SupervisorDeliveryClamp.Clamp(proposed, declared)!.OpenPullRequest.ShouldBe(true);
    }

    [Fact]
    public void The_operator_wins_per_field_not_per_whole_object()
    {
        // The operator declared ONLY OpenPullRequest; the model's own TargetBranch proposal fills the gap.
        var proposed = new DeliverySpec { OpenPullRequest = true, TargetBranch = "feature/x" };
        var declared = new DeliverySpec { OpenPullRequest = false };

        var clamped = SupervisorDeliveryClamp.Clamp(proposed, declared)!;

        clamped.OpenPullRequest.ShouldBe(false, "the operator's own declared field wins");
        clamped.TargetBranch.ShouldBe("feature/x", "a field the operator never mentioned falls through to the model's own proposal — clamping is per field, not per object");
    }

    [Fact]
    public void The_clamp_is_pure_and_deterministic()
    {
        var proposed = new DeliverySpec { OpenPullRequest = true, TargetBranch = "main" };
        var declared = new DeliverySpec { TargetBranch = "release" };

        SupervisorDeliveryClamp.Clamp(proposed, declared).ShouldBe(SupervisorDeliveryClamp.Clamp(proposed, declared));
    }
}

using CodeSpace.Core.Services.Supervisor;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: pins S4 — the per-agent MODEL privilege gate (<see cref="SupervisorModelClamp.ClampModel"/>), the model
/// analogue of <see cref="SupervisorRepoClamp"/>. A model-authored per-agent model is GRANTED only a model in the
/// operator's allowed pool; an out-of-pool model fails CLOSED with <see cref="SupervisorModelAccessException"/>. An
/// empty / null pool = NO restriction (opt-in), so the pre-S4 spawn path is byte-identical.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorModelClampTests
{
    private static readonly string[] Pool = { "claude-opus-4-8", "gpt-5.4-codex" };

    [Fact]
    public void No_pool_passes_the_requested_model_through()
    {
        SupervisorModelClamp.ClampModel("any-model", "profile-model", allowedModels: null).ShouldBe("any-model");
        SupervisorModelClamp.ClampModel("any-model", "profile-model", allowedModels: Array.Empty<string>()).ShouldBe("any-model");
    }

    [Fact]
    public void No_pool_falls_back_to_the_profile_model_then_to_null()
    {
        SupervisorModelClamp.ClampModel(requestedModel: null, "profile-model", allowedModels: null).ShouldBe("profile-model");
        SupervisorModelClamp.ClampModel(requestedModel: "  ", profileModel: "  ", allowedModels: null).ShouldBeNull("blank → null → the harness default");
    }

    [Fact]
    public void The_requested_model_wins_over_the_profile_default()
    {
        SupervisorModelClamp.ClampModel("claude-opus-4-8", "gpt-5.4-codex", Pool).ShouldBe("claude-opus-4-8");
    }

    [Theory]
    [InlineData("claude-opus-4-8")]
    [InlineData("CLAUDE-OPUS-4-8")]   // membership is case-insensitive
    [InlineData("  gpt-5.4-codex  ")] // trimmed before the check
    public void A_model_in_the_pool_passes(string requested)
    {
        SupervisorModelClamp.ClampModel(requested, profileModel: null, Pool).ShouldBe(requested.Trim());
    }

    [Fact]
    public void A_model_outside_the_pool_fails_closed()
    {
        Should.Throw<SupervisorModelAccessException>(() => SupervisorModelClamp.ClampModel("rogue-model", profileModel: null, Pool));
    }

    [Fact]
    public void The_profile_default_is_also_clamped_to_the_pool()
    {
        // The effective model can come from the profile, not just the authored spec — it must be in the pool too.
        Should.Throw<SupervisorModelAccessException>(() => SupervisorModelClamp.ClampModel(requestedModel: null, "rogue-profile-model", Pool));
    }

    [Fact]
    public void No_named_model_is_not_clamped_even_with_a_pool()
    {
        // Neither authored nor profile model → the harness picks its own default; there is no NAME to gate, so the
        // pool (which bounds explicit choices) does not apply.
        SupervisorModelClamp.ClampModel(requestedModel: null, profileModel: null, Pool).ShouldBeNull();
    }
}

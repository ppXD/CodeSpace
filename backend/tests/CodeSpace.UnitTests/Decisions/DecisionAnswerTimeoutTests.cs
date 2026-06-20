using CodeSpace.Messages.Decisions;
using Shouldly;

namespace CodeSpace.UnitTests.Decisions;

/// <summary>
/// 🟢 Unit: the shared timeout-default factory <see cref="DecisionAnswer.Timeout"/> (Decision substrate D5b) — the ONE
/// source of truth both grains use (the node wait + the agent reaper) so a deadline produces a byte-identical default
/// answer either way. Pins: the configured DefaultAction becomes the sole selection; a null/blank default yields an empty
/// selection (left for convert-to-human / a human); always stamped Timeout + TimedOut + a non-silent rationale (AC3); the
/// caller's grain handle flows through as DecisionId.
/// </summary>
[Trait("Category", "Unit")]
public class DecisionAnswerTimeoutTests
{
    private static DecisionRequest Request(string? defaultAction) => new()
    {
        Id = Guid.NewGuid(),
        RootTraceId = Guid.NewGuid(),
        Scope = DecisionScopes.Agent,
        RequesterType = DecisionRequesterTypes.Agent,
        DecisionType = DecisionTypes.ChooseOne,
        Question = "which path?",
        Options = new[] { new DecisionOption { Id = "a", Label = "A" }, new DecisionOption { Id = "b", Label = "B" } },
        RiskLevel = DecisionRiskLevels.Low,
        Policy = DecisionPolicies.SupervisorFirst,
        DefaultAction = defaultAction,
        TimeoutAt = DateTimeOffset.UnixEpoch,
        DedupeKey = "k",
        ResumeBackend = DecisionResumeBackends.ToolLedger,
    };

    [Fact]
    public void A_configured_default_becomes_the_sole_selection_stamped_timeout()
    {
        var handle = Guid.NewGuid();

        var answer = DecisionAnswer.Timeout(Request(defaultAction: "a"), handle);

        answer.DecisionId.ShouldBe(handle, "the caller's grain handle flows through (the node uses the request id, the agent reaper the ledger row id)");
        answer.AnsweredBy.ShouldBe(DecisionAnsweredByKinds.Timeout);
        answer.SelectedOptions.ShouldBe(new[] { "a" });
        answer.TimedOut.ShouldBeTrue();
        answer.Rationale.ShouldNotBeNullOrWhiteSpace("a timeout answer is never silent (AC3)");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_default_yields_an_empty_selection(string? defaultAction)
    {
        var answer = DecisionAnswer.Timeout(Request(defaultAction), Guid.NewGuid());

        answer.SelectedOptions.ShouldBeEmpty("no configured default → no selection; the agent reaper leaves such a row Pending (convert-to-human is D5d)");
        answer.TimedOut.ShouldBeTrue();
    }
}

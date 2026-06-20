using System.Text.Json;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Messages.Decisions;
using Shouldly;

namespace CodeSpace.UnitTests.Decisions;

/// <summary>
/// Pins the defense-in-depth answer validation the queue write (Decision substrate D3b) applies against the stashed
/// envelope (<see cref="DecisionAnswerService.Validate"/>): an option-bearing decision rejects unknown / empty option
/// choices, an option-less decision requires non-empty free text, and a missing / malformed envelope passes (a
/// projection quirk must never block a legitimate answer — the durable resume is itself tolerant).
/// </summary>
[Trait("Category", "Unit")]
public class DecisionAnswerValidationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string Envelope(params string[] optionIds) => JsonSerializer.Serialize(new DecisionRequest
    {
        Id = Guid.NewGuid(),
        RootTraceId = Guid.NewGuid(),
        Scope = DecisionScopes.Agent,
        RequesterType = DecisionRequesterTypes.Agent,
        DecisionType = optionIds.Length > 0 ? DecisionTypes.ChooseOne : DecisionTypes.FreeText,
        Question = "pick",
        Options = optionIds.Select(id => new DecisionOption { Id = id, Label = id.ToUpperInvariant() }).ToList(),
        RiskLevel = DecisionRiskLevels.Low,
        Policy = DecisionPolicies.HumanRequired,
        TimeoutAt = DateTimeOffset.UnixEpoch,
        DedupeKey = "k",
        ResumeBackend = DecisionResumeBackends.ToolLedger,
    }, Json);

    [Fact]
    public void A_valid_option_choice_passes()
    {
        DecisionAnswerService.Validate(Envelope("a", "b"), new[] { "b" }, null, out var error).ShouldBeTrue();
        error.ShouldBeNull();
    }

    [Fact]
    public void An_unknown_option_is_rejected()
    {
        DecisionAnswerService.Validate(Envelope("a", "b"), new[] { "z" }, null, out var error).ShouldBeFalse();
        error.ShouldContain("z");
    }

    [Fact]
    public void An_option_bearing_decision_with_no_selection_is_rejected()
    {
        DecisionAnswerService.Validate(Envelope("a", "b"), Array.Empty<string>(), "just text", out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void A_free_text_decision_requires_non_empty_text()
    {
        DecisionAnswerService.Validate(Envelope(), Array.Empty<string>(), "   ", out var blank).ShouldBeFalse();
        blank.ShouldNotBeNull();

        DecisionAnswerService.Validate(Envelope(), Array.Empty<string>(), "my answer", out var ok).ShouldBeTrue();
        ok.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{ \"incomplete\": true }")]
    public void A_missing_or_malformed_envelope_passes_so_a_quirk_never_blocks_a_legit_answer(string? envelopeJson)
    {
        DecisionAnswerService.Validate(envelopeJson, new[] { "anything" }, null, out var error).ShouldBeTrue();
        error.ShouldBeNull();
    }
}

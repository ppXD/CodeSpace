using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Review;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Review;

/// <summary>
/// 🟢 Unit: an artifact too large for one review call is reviewed in parts the reviewer row's window holds. A review
/// sized to one call failed whenever the reviewed party chose a large change, and a review that cannot run now holds
/// the result — so without parts an honest large change could never be approved, and a row with a small window refused
/// changes far below the render's own bound. The pure split and merge are pinned here, and the real critic's use of
/// them against a scripted reviewer.
/// </summary>
[Trait("Category", "Unit")]
public sealed class CriticArtifactPartsTests
{
    [Fact]
    public void An_artifact_that_fits_is_one_part_and_nothing_else()
    {
        CriticArtifactParts.Split("a small diff\n", 4_096).ShouldBe(new[] { "a small diff\n" });
        CriticArtifactParts.Split("", 4_096).ShouldBe(new[] { "" });
    }

    [Theory]
    [InlineData(4_096)]
    [InlineData(10_000)]
    public void The_parts_are_the_artifact_each_within_the_bound_and_cut_after_a_line(int maxBytes)
    {
        var artifact = string.Concat(Enumerable.Range(0, 2_000).Select(i => $"+ line {i} — ünïcödé 😀 payload\n"));

        var parts = CriticArtifactParts.Split(artifact, maxBytes);

        string.Concat(parts).ShouldBe(artifact, "nothing is lost or repeated between parts");
        parts.ShouldAllBe(part => Encoding.UTF8.GetByteCount(part) <= maxBytes);
        parts.Take(parts.Count - 1).ShouldAllBe(part => part.EndsWith('\n'), "a part ends at a line break when one fits");
    }

    [Fact]
    public void A_line_longer_than_a_part_is_cut_without_splitting_a_surrogate_pair()
    {
        var artifact = string.Concat(Enumerable.Repeat("😀", 5_000));

        var parts = CriticArtifactParts.Split(artifact, 4_097);

        string.Concat(parts).ShouldBe(artifact);
        parts.Select(part => (Last: part[part.Length - 1], First: part[0])).ShouldAllBe(edge => !char.IsHighSurrogate(edge.Last) && !char.IsLowSurrogate(edge.First));
    }

    [Fact]
    public void A_row_with_no_declared_window_gets_the_default_part_size()
    {
        CriticArtifactParts.PartBytes(null, Framing()).ShouldBe(CriticArtifactParts.DefaultPartBytes);
    }

    [Fact]
    public void A_declared_window_sizes_each_part_so_the_whole_call_passes_the_preflight()
    {
        var framing = Framing();
        var partBytes = CriticArtifactParts.PartBytes(32_000, framing).ShouldNotBeNull();

        LlmContextWindowBudget.RequiresCompaction(framing with { UserPrompt = framing.UserPrompt + new string('x', partBytes) }, 32_000).ShouldBeFalse("a full part fits the window by the repository's own estimator");
        LlmContextWindowBudget.RequiresCompaction(framing with { UserPrompt = framing.UserPrompt + new string('x', partBytes + 3_000) }, 32_000).ShouldBeTrue("fixture check: the part is sized to the window, not far below it");
    }

    [Fact]
    public void A_window_too_small_for_any_part_is_no_size_at_all()
    {
        CriticArtifactParts.PartBytes(2_000, Framing()).ShouldBeNull();
    }

    [Fact]
    public void A_gate_over_parts_approves_only_when_every_part_approved_and_keeps_every_issue()
    {
        var merged = CriticArtifactParts.Merge(ReviewMode.Gate, new[]
        {
            new CriticVerdict { Mode = ReviewMode.Gate, Approved = true, Score = 90, Rationale = "fine" },
            new CriticVerdict { Mode = ReviewMode.Gate, Approved = false, Score = 10, Rationale = "auth bypass", Issues = new[] { new CriticIssue { Text = "backdoor", Severity = CriticSeverity.Blocker } } },
        });

        merged.Approved.ShouldBeFalse();
        merged.Score.ShouldBe(10);
        merged.Issues.ShouldHaveSingleItem().Text.ShouldBe("backdoor");
        merged.Rationale.ShouldBe("[part 1 of 2] fine [part 2 of 2] auth bypass");
    }

    [Fact]
    public void An_improve_review_over_parts_keeps_each_critique_that_warrants_a_revision()
    {
        var merged = CriticArtifactParts.Merge(ReviewMode.Improve, new[]
        {
            new CriticVerdict { Mode = ReviewMode.Improve, Critique = null, Rationale = "nitpicks only" },
            new CriticVerdict { Mode = ReviewMode.Improve, Critique = "add the missing test", Rationale = "untested path" },
        });

        merged.Critique.ShouldBe("[part 2 of 2] add the missing test");
    }

    [Fact]
    public async Task The_real_critic_reviews_each_part_once_and_merges_the_verdicts()
    {
        var client = new InstructionFollowingCritic();
        var critic = new LlmStructuredCritic(new SingleClientRegistry(client), new StubReviewerPool(), NullLogger<LlmStructuredCritic>.Instance);

        var artifact = new string('a', 250_000) + "\n" + ReviewModelStubs.FlawMarker + "\n";
        var verdict = await critic.ReviewAsync(Request(artifact), Guid.NewGuid(), reviewerModelId: null, CancellationToken.None);

        client.Requests.Count.ShouldBe(2);
        client.Requests[0].UserPrompt.ShouldContain("this is part 1 of 2");
        client.Requests[1].UserPrompt.ShouldContain("this is part 2 of 2");
        client.Requests.Select(r => r.UserPrompt).ShouldAllBe(prompt => !ReviewModelStubs.InstructionsOf(prompt).Contains(ReviewModelStubs.FlawMarker), "each part rides its own data block");
        verdict.Failed.ShouldBeFalse();
        verdict.Approved.ShouldBeFalse("the second part carries the flaw");
        verdict.ReviewerModel.ShouldBe("stub-reviewer");
    }

    [Fact]
    public async Task One_part_keeps_the_prompt_the_critic_always_sent()
    {
        var client = new InstructionFollowingCritic();
        var critic = new LlmStructuredCritic(new SingleClientRegistry(client), new StubReviewerPool(), NullLogger<LlmStructuredCritic>.Instance);

        await critic.ReviewAsync(Request("a small diff"), Guid.NewGuid(), reviewerModelId: null, CancellationToken.None);

        client.Requests.ShouldHaveSingleItem().UserPrompt.ShouldNotContain("part 1 of");
    }

    [Fact]
    public async Task An_artifact_past_the_most_parts_a_review_may_make_is_not_reviewed_and_makes_no_call()
    {
        var client = new InstructionFollowingCritic();
        var critic = new LlmStructuredCritic(new SingleClientRegistry(client), new StubReviewerPool(contextWindowTokens: 16_000), NullLogger<LlmStructuredCritic>.Instance);

        var verdict = await critic.ReviewAsync(Request(new string('a', 800_000)), Guid.NewGuid(), reviewerModelId: null, CancellationToken.None);

        verdict.Failed.ShouldBeTrue();
        verdict.Rationale.ShouldContain($"does not fit {CriticArtifactParts.MaxParts} review calls");
        client.Requests.ShouldBeEmpty("a review that cannot cover the artifact spends nothing on part of it");
    }

    private static CriticRequest Request(string artifact) => new() { Mode = ReviewMode.Gate, ArtifactKind = CriticArtifactKinds.AgentChange, Artifact = artifact, Goal = "fix auth" };

    private static StructuredLLMCompletionRequest Framing() => new()
    {
        Model = "stub-model",
        SystemPrompt = new string('s', 3_000),
        UserPrompt = new string('u', 600),
        JsonSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement,
        MaxOutputTokens = 2_048,
        Credential = new ResolvedModelCredential { Provider = "Stub", ApiKey = "sk-fake" },
    };
}

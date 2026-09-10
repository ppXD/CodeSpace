using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Supervisor;

/// <summary>
/// 🟢 Unit: <see cref="SupervisorResolverRecipe.BuildInstruction"/> — the deterministic resolver-agent goal text
/// (resolver loop #379, S2), no model. Pins that a REAL failure (<see cref="SupervisorIntegrationOutcome.FailingContributions"/>)
/// and a MERELY SKIPPED survivor (<see cref="SupervisorIntegrationOutcome.SkippedContributions"/> — blocked only
/// because a different contribution's failure stopped the set before its turn) render under their OWN distinct
/// headers, never folded into one undifferentiated "failed to integrate" list.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorResolverRecipeTests
{
    private static SupervisorIntegrationOutcome Conflict(IReadOnlyList<string>? failing = null, IReadOnlyList<string>? skipped = null, IReadOnlyList<string>? conflictedFiles = null) => new()
    {
        Status = "Conflicted",
        ConflictedFiles = conflictedFiles ?? Array.Empty<string>(),
        FailingContributions = failing ?? Array.Empty<string>(),
        SkippedContributions = skipped ?? Array.Empty<string>(),
    };

    [Fact]
    public void Names_the_failing_contributions_under_their_own_header()
    {
        var instruction = SupervisorResolverRecipe.BuildInstruction("Ship the fix", Conflict(failing: new[] { "agent-b: textual conflict" }), new[] { "codespace/agent/a", "codespace/agent/b" });

        instruction.ShouldContain("The contribution(s) that failed to integrate, and why:");
        instruction.ShouldContain("- agent-b: textual conflict");
    }

    [Fact]
    public void Names_a_skipped_survivor_under_a_header_distinct_from_the_failing_one()
    {
        var instruction = SupervisorResolverRecipe.BuildInstruction("Ship the fix",
            Conflict(failing: new[] { "agent-b: textual conflict" }, skipped: new[] { "agent-c: not integrated — an earlier contribution conflicted" }),
            new[] { "codespace/agent/a", "codespace/agent/b", "codespace/agent/c" });

        instruction.ShouldContain("The contribution(s) that failed to integrate, and why:");
        instruction.ShouldContain("- agent-b: textual conflict");
        instruction.ShouldContain("The contribution(s) not attempted");
        instruction.ShouldContain("- agent-c: not integrated — an earlier contribution conflicted");

        // The two lists render as SEPARATE labeled blocks, failing block first — a skipped survivor must never read
        // as an equal failure ahead of (or folded into) the genuine defect.
        var failingBlockStart = instruction.IndexOf("The contribution(s) that failed to integrate", StringComparison.Ordinal);
        var skippedBlockStart = instruction.IndexOf("The contribution(s) not attempted", StringComparison.Ordinal);
        failingBlockStart.ShouldBeLessThan(skippedBlockStart, "the failing block leads — the skipped block is the distinct, secondary header");
    }

    [Fact]
    public void A_skipped_survivor_with_no_failing_contribution_still_gets_its_own_header()
    {
        // The pure-preflight-refusal shape: EVERY survivor is "not attempted", none is individually a real failure.
        var instruction = SupervisorResolverRecipe.BuildInstruction("Ship the fix",
            Conflict(skipped: new[] { "agent-a: not attempted — the set was refused before integration began" }),
            new[] { "codespace/agent/a" });

        instruction.ShouldNotContain("failed to integrate", customMessage: "no contribution had a defect of its own");
        instruction.ShouldContain("The contribution(s) not attempted");
        instruction.ShouldContain("- agent-a: not attempted — the set was refused before integration began");
    }

    [Fact]
    public void Omits_both_blocks_when_neither_list_carries_a_contribution()
    {
        var instruction = SupervisorResolverRecipe.BuildInstruction("Ship the fix", Conflict(), new[] { "codespace/agent/a" });

        instruction.ShouldNotContain("failed to integrate");
        instruction.ShouldNotContain("not attempted");
    }
}

using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: S2's server FALLBACK for <see cref="SupervisorPlannedSubtask.ExpectsChanges"/> — the model's own
/// declaration always wins; only when it is absent does the verb-based inference apply, defaulting to <c>true</c>
/// (byte-identical fail-closed) for anything not matching the narrow read-only allowlist.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorSubtaskExpectationsTests
{
    [Fact]
    public void An_explicit_declaration_always_wins_regardless_of_the_instruction_text()
    {
        Resolve("investigate the root cause", expectsChanges: true).ShouldBeTrue("an explicit true overrides the read-only-verb inference");
        Resolve("implement the fix", expectsChanges: false).ShouldBeFalse("an explicit false overrides the default-true inference");
    }

    [Theory]
    [InlineData("investigate the flaky test")]
    [InlineData("Investigate the flaky test")]
    [InlineData("ANALYZE the logs for the root cause")]
    [InlineData("analyse the logs")]
    [InlineData("research prior art for rate limiting")]
    [InlineData("review the PR for security issues")]
    [InlineData("audit the auth flow")]
    [InlineData("inspect the failing pipeline")]
    [InlineData("report on the outage timeline")]
    [InlineData("document the API surface")]
    [InlineData("summarize the findings")]
    [InlineData("summarise the findings")]
    public void A_leading_read_only_verb_infers_no_changes_expected_when_unset(string instruction) =>
        Resolve(instruction, expectsChanges: null).ShouldBeFalse($"'{instruction}' opens with a read-only verb");

    [Theory]
    [InlineData("implement the fix")]
    [InlineData("add input validation")]
    [InlineData("fix the null check")]
    [InlineData("write unit tests")]
    [InlineData("refactor the module")]
    [InlineData("Investigate")]   // the bare verb alone, no object — still matches (defensive: still a legitimate leading-verb match)
    public void Anything_else_defaults_to_expecting_changes_when_unset(string instruction)
    {
        if (instruction == "Investigate") { Resolve(instruction, expectsChanges: null).ShouldBeFalse("a bare read-only verb with no object still matches the leading-word check"); return; }

        Resolve(instruction, expectsChanges: null).ShouldBeTrue($"'{instruction}' does not open with a read-only verb — the safe default");
    }

    [Fact]
    public void A_read_only_verb_must_be_the_FIRST_word_not_merely_present()
    {
        Resolve("implement the fix, then document it", expectsChanges: null).ShouldBeTrue("'document' appears but is not the LEADING verb — the instruction still defaults to expecting changes");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_instruction_defaults_to_expecting_changes(string instruction) =>
        Resolve(instruction, expectsChanges: null).ShouldBeTrue("nothing to infer from — the safe default");

    [Fact]
    public void Punctuation_after_the_leading_verb_does_not_defeat_the_match() =>
        Resolve("Investigate: why is the queue backing up?", expectsChanges: null).ShouldBeFalse("a trailing colon on the leading verb is trimmed before matching");

    private static bool Resolve(string instruction, bool? expectsChanges) =>
        SupervisorSubtaskExpectations.Resolve(new SupervisorPlannedSubtask { Id = "s", Title = "t", Instruction = instruction, ExpectsChanges = expectsChanges });
}

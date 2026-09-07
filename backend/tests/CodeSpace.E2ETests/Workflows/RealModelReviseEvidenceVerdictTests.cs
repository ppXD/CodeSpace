using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class RealModelReviseEvidenceVerdictTests
{
    [Fact]
    public void A_successful_grade_alone_cannot_qualify_an_outer_revision()
    {
        var witness = Valid();
        RealModelReviseEvidenceE2ETests.MeetsWitness(witness).ShouldBeTrue();
        RealModelReviseEvidenceE2ETests.MeetsWitness(witness with { NativeStarts = 1 }).ShouldBeFalse();
        RealModelReviseEvidenceE2ETests.MeetsWitness(witness with { NativeTools = 0 }).ShouldBeFalse();
        RealModelReviseEvidenceE2ETests.MeetsWitness(witness with { InitialWasObserved = false }).ShouldBeFalse();
        RealModelReviseEvidenceE2ETests.MeetsWitness(witness with { Result = witness.Result with { ReviseRounds = 0 } }).ShouldBeFalse();
    }

    [Theory]
    [InlineData(AgentRunStatus.Failed)]
    [InlineData(AgentRunStatus.TimedOut)]
    [InlineData(AgentRunStatus.Cancelled)]
    public void A_partial_or_failed_attempt_cannot_qualify(AgentRunStatus status)
    {
        var witness = Valid();
        RealModelReviseEvidenceE2ETests.MeetsWitness(witness with { Result = witness.Result with { Status = status } }).ShouldBeFalse();
    }

    [Fact]
    public void Missing_native_model_usage_or_another_attempts_answer_cannot_qualify()
    {
        var witness = Valid();
        RealModelReviseEvidenceE2ETests.MeetsWitness(witness with { Actual = "different-attempt" }).ShouldBeFalse();
        RealModelReviseEvidenceE2ETests.MeetsWitness(witness with { Result = witness.Result with { TokenUsage = null } }).ShouldBeFalse();
        RealModelReviseEvidenceE2ETests.MeetsWitness(witness with { Result = witness.Result with { TokenUsage = new AgentTokenUsage { InputTokens = 10, OutputTokens = 0 } } }).ShouldBeFalse();
        RealModelReviseEvidenceE2ETests.MeetsWitness(witness with { Result = witness.Result with { SessionId = null } }).ShouldBeFalse();
        RealModelReviseEvidenceE2ETests.MeetsWitness(witness with { Result = witness.Result with { AcceptancePassed = null } }).ShouldBeFalse();
    }

    [Fact]
    public void The_existing_live_lane_selects_and_counts_the_exact_revision_case()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, ".github"))) root = root.Parent;
        root.ShouldNotBeNull();
        var workflow = File.ReadAllText(Path.Combine(root.FullName, ".github/workflows/real-model.yml"));
        workflow.ShouldContain("FullyQualifiedName~RealModelReviseEvidence");
        workflow.ShouldContain("CodeSpace.E2ETests.Workflows.RealModelReviseEvidenceE2ETests.A_live_CLI_revision_uses_the_actual_failed_oracle_diagnosis");
    }

    private static RealModelReviseEvidenceE2ETests.Witness Valid() => new()
    {
        Result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", AcceptancePassed = true, ReviseRounds = 1, TokenUsage = new AgentTokenUsage { InputTokens = 10, OutputTokens = 5 }, SessionId = "actual-session", Model = "observed-model" },
        Actual = "fresh-diagnosis", Expected = "fresh-diagnosis", NativeStarts = 2, NativeTools = 1, InitialWasObserved = true,
    };
}

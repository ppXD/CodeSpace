using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Sessions;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions;

/// <summary>
/// Pins what a durable side-effect receipt tells the model about a FAILED ledger row. A row a reviewer rejected carries
/// <see cref="ToolCallApprovalResolver.RejectedError"/> and nothing ran, so it must not read as an executed call whose
/// outcome is uncertain; every other failure keeps that reading, because its effect may have landed.
/// </summary>
[Trait("Category", "Unit")]
public class SessionEffectReceiptTextTests
{
    [Theory]
    [InlineData(ToolCallApprovalResolver.RejectedError, "observation=rejected-before-execution; nothing ran", "external outcome may be uncertain")]
    [InlineData("merge conflict", "observation=recorded-failure; external outcome may be uncertain", "rejected-before-execution")]
    public void A_failed_receipt_says_nothing_ran_only_when_a_reviewer_rejected_the_call(string error, string expected, string absent)
    {
        var text = SessionEffectReceiptText.Render(new SessionEffectReceiptPage { Items = [FailedReceipt(error)] }, launchDigest: false);

        text.ShouldContain(expected);
        text.ShouldNotContain(absent);
    }

    private static SessionEffectReceipt FailedReceipt(string error) => new()
    {
        Id = Guid.NewGuid(), WorkflowRunId = Guid.NewGuid(), AgentRunId = Guid.NewGuid(), ToolKind = "git.open_pr",
        InputHash = new string('0', 64), Status = "Failed", Error = error, ErrorCharacters = error.Length, CreatedDate = DateTimeOffset.UnixEpoch,
    };
}

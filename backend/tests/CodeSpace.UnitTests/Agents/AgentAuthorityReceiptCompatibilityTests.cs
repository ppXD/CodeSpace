using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Review;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

public sealed class AgentAuthorityReceiptCompatibilityTests
{
    [Fact]
    public void Legacy_receipts_keep_their_exact_canonical_bytes_and_hash()
    {
        const string original = """{"version":1,"policyVersion":"team-permissions-intersection/v1","teamId":"11111111-1111-1111-1111-111111111111","logicalRunId":"22222222-2222-2222-2222-222222222222","sourceKind":"standalone","definitionHash":"","activationId":null,"activationRevision":null,"parentRunId":null,"grantedCeiling":"Standard","issuedAt":"2026-09-07T00:00:00+00:00","subjects":[]}""";
        var receipt = JsonSerializer.Deserialize<AgentExecutionAuthority>(original, AgentJson.Options)!;
        var current = JsonSerializer.Serialize(receipt, AgentJson.Options);
        current.ShouldBe(original);
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(current))).ShouldBe("c6a00a25b5ab25f832bf409db4ffcf122fba89a76d8ae372bac34862cd77d0b6");
        receipt.ParentAgentRunId.ShouldBeNull();
        receipt.ParentAuthorityHash.ShouldBeNull();
    }

    [Fact]
    public void Parent_observer_credentials_do_not_serialize_into_review_or_task_inputs()
    {
        var owner = new AgentRunOwnerToken(Guid.NewGuid(), Guid.NewGuid(), 7);
        var spec = new AgentReviewSpec { ParentOwner = owner, SubjectInstructions = "inspect", RepositoryId = Guid.NewGuid(), TeamId = Guid.NewGuid(), IterationKey = "#review" };
        JsonSerializer.Serialize(spec, AgentJson.Options).ShouldNotContain(owner.OwnerId.ToString());
        var task = AgentReviewRunner.BuildReviewTask(spec, "codex-cli");
        JsonSerializer.Serialize(task, AgentJson.Options).ShouldNotContain(owner.OwnerId.ToString());
        task.ExecutionAuthority.ShouldBeNull("only persisted admission can stamp a receipt");
        task.EnableMcpEndpoint.ShouldBe(false);
        task.Workspace!.Repositories.ShouldAllBe(r => r.Access == WorkspaceAccess.Read);
    }
}

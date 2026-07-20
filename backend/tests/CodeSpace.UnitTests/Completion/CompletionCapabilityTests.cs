using CodeSpace.Core.Services.Completion;
using CodeSpace.Messages.Contracts;
using Shouldly;

namespace CodeSpace.UnitTests.Completion;

/// <summary>
/// 🟢 Unit: pins P2b-3 — the closed capability vocabulary (keys + committed qualification statuses, Rule-8
/// style) and the pure derivation of WHAT a run was asked for from its own staked obligation set. An unknown
/// key resolves null — the authority reads that as Unsupported (Lock Clause 4), pinned at the decider tier.
/// </summary>
[Trait("Category", "Unit")]
public class CompletionCapabilityTests
{
    [Fact]
    public void The_registry_table_is_pinned()
    {
        var registry = new CompletionCapabilityRegistry();

        registry.Resolve(CapabilityKeys.GitBranch)!.Qualification.ShouldBe(QualificationStatus.ShadowEvaluation);
        registry.Resolve(CapabilityKeys.GitPatch)!.Qualification.ShouldBe(QualificationStatus.ShadowEvaluation);
        registry.Resolve(CapabilityKeys.InlineAnswer)!.Qualification.ShouldBe(QualificationStatus.OpenDevelopment);

        registry.Resolve("image").ShouldBeNull("a novel ask is honestly UNKNOWN — the authority parks it as Unsupported, never a silent attempt");
        registry.Resolve("GIT-BRANCH").ShouldBeNull("keys are exact — the vocabulary is closed, not fuzzy");
    }

    [Fact]
    public void The_key_literals_are_pinned()
    {
        // Wire values on durable records and reasons — a rename is a migration decision, not a refactor.
        CapabilityKeys.GitBranch.ShouldBe("git-branch");
        CapabilityKeys.GitPatch.ShouldBe("git-patch");
        CapabilityKeys.InlineAnswer.ShouldBe("inline-answer");
    }

    [Theory]
    [InlineData(true, true, "git-branch")]    // delivery Required wins — the branch surface was asked
    [InlineData(false, true, "git-patch")]    // output Required without delivery — capture-only ask
    [InlineData(false, false, "inline-answer")] // neither — the answer itself is the deliverable
    public void The_asked_capability_derives_from_the_staked_obligations(bool deliveryRequired, bool outputRequired, string expected)
    {
        var requirements = new List<RequirementEnvelope>
        {
            new() { RequirementRef = "acceptance:s1", Kind = ContractKinds.Acceptance, Requiredness = Requiredness.Required, Authority = ContractAuthority.ModelProposal, ContractSchemaVersion = "1" },
            new() { RequirementRef = "delivery:s1", Kind = ContractKinds.Delivery, Requiredness = deliveryRequired ? Requiredness.Required : Requiredness.ServerPolicyAuthorizedNotApplicable, Authority = deliveryRequired ? ContractAuthority.ModelProposal : ContractAuthority.ServerPolicy, ContractSchemaVersion = "1" },
            new() { RequirementRef = "output:s1", Kind = ContractKinds.Output, Requiredness = outputRequired ? Requiredness.Required : Requiredness.ServerPolicyAuthorizedNotApplicable, Authority = outputRequired ? ContractAuthority.ModelProposal : ContractAuthority.ServerPolicy, ContractSchemaVersion = "1" },
        };

        CompletionCapability.Derive(requirements).ShouldBe(expected);
    }

    [Fact]
    public void An_authorized_NA_stake_never_selects_a_surface()
    {
        // The explicit "not asked" is exactly that — a read-only unit's NA delivery/output rows derive the
        // inline answer, not a git surface.
        var requirements = new List<RequirementEnvelope>
        {
            new() { RequirementRef = "delivery:s1", Kind = ContractKinds.Delivery, Requiredness = Requiredness.ServerPolicyAuthorizedNotApplicable, Authority = ContractAuthority.ServerPolicy, ContractSchemaVersion = "1" },
        };

        CompletionCapability.Derive(requirements).ShouldBe(CapabilityKeys.InlineAnswer);
    }
}

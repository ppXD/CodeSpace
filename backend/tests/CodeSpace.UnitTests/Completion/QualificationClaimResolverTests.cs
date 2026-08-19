using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Messages.Contracts;
using Shouldly;

namespace CodeSpace.UnitTests.Completion;

/// <summary>
/// 🟢 Unit: Q4's claim fold — the ONE lawful statement of measured performance. Pins: no current receipt =
/// Unmeasured backed by nothing; the highest-granting current receipt backs the claim VERBATIM (Sealed outranks
/// Shadow even when the shadow round is newer; equal grants go to the latest round); a claim never exceeds what
/// its backing receipt granted.
/// </summary>
[Trait("Category", "Unit")]
public class QualificationClaimResolverTests
{
    [Fact]
    public void No_current_receipt_reads_unmeasured_backed_by_nothing()
    {
        var claim = QualificationClaimResolver.Fold("supervisor", "git-branch", Array.Empty<QualificationReceipt>());

        claim.Performance.ShouldBe(PerformanceQualification.Unmeasured);
        claim.ReceiptId.ShouldBeNull("an unmeasured claim must point at nothing — there is no round to audit");
        claim.SuiteDigest.ShouldBeNull();
        claim.ExpiresAt.ShouldBeNull();
    }

    [Fact]
    public void A_sealed_receipt_outranks_a_newer_shadow_round()
    {
        var sealedReceipt = Receipt(PerformanceQualification.Sealed, daysAgo: 10);
        var shadowReceipt = Receipt(PerformanceQualification.Shadow, daysAgo: 1);

        var claim = QualificationClaimResolver.Fold("supervisor", "git-branch", new[] { shadowReceipt, sealedReceipt });

        claim.Performance.ShouldBe(PerformanceQualification.Sealed, "the standing claim is the strongest CURRENT receipt — a later shadow round records evidence, it does not demote the seal");
        claim.ReceiptId.ShouldBe(sealedReceipt.Id);
        claim.SuiteDigest.ShouldBe(sealedReceipt.SuiteDigest);
        claim.ExpiresAt.ShouldBe(sealedReceipt.ExpiresAt);
    }

    [Fact]
    public void Equal_grants_go_to_the_latest_round()
    {
        var older = Receipt(PerformanceQualification.Shadow, daysAgo: 10);
        var newer = Receipt(PerformanceQualification.Shadow, daysAgo: 1);

        QualificationClaimResolver.Fold("supervisor", "git-branch", new[] { older, newer }).ReceiptId.ShouldBe(newer.Id);
    }

    [Fact]
    public void The_seal_and_cohort_are_composed_from_the_backing_receipts_own_columns()
    {
        var receipt = Receipt(PerformanceQualification.Sealed, daysAgo: 1);
        receipt.VerifierBundleJson = """{"harness":"codex-cli","model":"gpt-x","modelCredentialId":null}""";
        receipt.CohortJson = $$"""{"teamId":"{{Guid.NewGuid()}}","mode":"supervisor","tier":"internal-qualification","completionPolicyVersion":2}""";

        var claim = QualificationClaimResolver.Fold("supervisor", "git-branch", new[] { receipt });

        claim.Seal.ShouldNotBeNull();
        claim.Seal.CapabilityKey.ShouldBe("git-branch");
        claim.Seal.SuiteDigest.ShouldBe(receipt.SuiteDigest, "the seal summarizes the row it came from — it can never drift from it");
        claim.Seal.VerifierBundle.ShouldNotBeNull().Harness.ShouldBe("codex-cli", "the standing names WHO earned it");
        claim.Cohort.ShouldNotBeNull().Tier.ShouldBe("internal-qualification");
        claim.Cohort.CompletionPolicyVersion.ShouldBe(2, "a standing earned under one protocol revision never silently covers another");
    }

    [Fact]
    public void Legacy_ad_hoc_json_reads_no_identity_never_a_half_filled_one()
    {
        var receipt = Receipt(PerformanceQualification.Sealed, daysAgo: 1);   // helper leaves both json columns at their "{}" default

        var claim = QualificationClaimResolver.Fold("supervisor", "git-branch", new[] { receipt });

        claim.Cohort.ShouldBeNull("a cohort missing its required keys is no cohort");
        claim.Seal.ShouldNotBeNull("capability + digest are the receipt's own columns — always present");
        claim.Seal.VerifierBundle.ShouldBeNull("legacy json without the bundle keys must read null, never an empty bundle pretending someone judged");
    }

    [Fact]
    public void The_claim_copies_the_grant_verbatim_never_above_it()
    {
        var shadowReceipt = Receipt(PerformanceQualification.Shadow, daysAgo: 1);

        var claim = QualificationClaimResolver.Fold("supervisor", "git-branch", new[] { shadowReceipt });

        claim.Performance.ShouldBe(PerformanceQualification.Shadow, "measured evidence exists — but only a sealed receipt may back a Sealed statement");
        claim.ReceiptId.ShouldBe(shadowReceipt.Id);
    }

    private static QualificationReceipt Receipt(PerformanceQualification granted, int daysAgo) => new()
    {
        Id = Guid.NewGuid(),
        Mode = "supervisor",
        CapabilityKey = "git-branch",
        SuiteDigest = "sha256:suite-" + granted.ToString().ToLowerInvariant(),
        GrantedPerformance = granted,
        EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-daysAgo),
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
    };
}

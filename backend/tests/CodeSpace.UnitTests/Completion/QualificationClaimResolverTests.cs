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

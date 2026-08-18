using Autofac;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Completion;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Contracts;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 High fidelity (real Postgres + real receipt store + real resolver): Q4's claim gate end to end — a minted
/// sealed receipt backs a Sealed claim carrying its identity; REVOKING the receipt downgrades the very same query
/// at read time with no code change; a lapsed seal falls back to the next-best current receipt; the board covers
/// exactly the registered (mode × capability) grid.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class QualificationClaimFlowTests
{
    private readonly PostgresFixture _fixture;

    public QualificationClaimFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_sealed_receipt_backs_the_claim_and_revocation_downgrades_it_at_read_time()
    {
        var mode = UniqueMode();

        using var scope = _fixture.BeginScope();
        var store = scope.Resolve<IQualificationReceiptStore>();
        var resolver = scope.Resolve<IQualificationClaimResolver>();

        var receipt = Receipt(mode, PerformanceQualification.Sealed);
        await store.AppendAsync(receipt, CancellationToken.None);

        var claim = await resolver.ResolveAsync(mode, "git-branch", DateTimeOffset.UtcNow, CancellationToken.None);
        claim.Performance.ShouldBe(PerformanceQualification.Sealed);
        claim.ReceiptId.ShouldBe(receipt.Id, "the claim must carry the round that earned it — auditable, never a bare adjective");
        claim.SuiteDigest.ShouldBe(receipt.SuiteDigest);

        (await store.RevokeAsync(receipt.Id, CancellationToken.None)).ShouldBeTrue();

        var revoked = await resolver.ResolveAsync(mode, "git-branch", DateTimeOffset.UtcNow, CancellationToken.None);
        revoked.Performance.ShouldBe(PerformanceQualification.Unmeasured, "revocation must change what the SAME query answers — no code change, no cache to flush");
        revoked.ReceiptId.ShouldBeNull();
    }

    [Fact]
    public async Task A_lapsed_seal_falls_back_to_the_next_best_current_receipt()
    {
        var mode = UniqueMode();

        using var scope = _fixture.BeginScope();
        var store = scope.Resolve<IQualificationReceiptStore>();

        var lapsedSeal = Receipt(mode, PerformanceQualification.Sealed, effectiveDaysAgo: 40, validityDays: 30);
        var currentShadow = Receipt(mode, PerformanceQualification.Shadow);
        await store.AppendAsync(lapsedSeal, CancellationToken.None);
        await store.AppendAsync(currentShadow, CancellationToken.None);

        var claim = await scope.Resolve<IQualificationClaimResolver>().ResolveAsync(mode, "git-branch", DateTimeOffset.UtcNow, CancellationToken.None);

        claim.Performance.ShouldBe(PerformanceQualification.Shadow, "a lapsed seal is history, not a standing claim — re-qualification is owed before Sealed may be stated again");
        claim.ReceiptId.ShouldBe(currentShadow.Id);
    }

    [Fact]
    public async Task The_board_covers_exactly_the_registered_grid()
    {
        using var scope = _fixture.BeginScope();

        var board = await scope.Resolve<IQualificationClaimResolver>().ResolveBoardAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        board.Rows.Count.ShouldBe(9, "3 registered modes × 3 registered capabilities — a new lane or capability must consciously grow this board");
        board.Rows.Select(r => (r.Mode, r.CapabilityKey)).ShouldBeUnique();
        board.Rows.ShouldContain(r => r.Mode == RunModeKeys.Supervisor && r.CapabilityKey == CapabilityKeys.GitBranch);
        board.Rows.ShouldAllBe(r => r.Mode != RunModeKeys.Generic, "the unregistered generic mode has no conformance story and must never appear on the claim board");
    }

    private static string UniqueMode() => "claim-" + Guid.NewGuid().ToString("N")[..8];

    private static QualificationReceipt Receipt(string mode, PerformanceQualification granted, int effectiveDaysAgo = 0, int validityDays = 30) => new()
    {
        Id = Guid.NewGuid(),
        Mode = mode,
        CapabilityKey = "git-branch",
        SuiteDigest = "sha256:claim-suite-" + Guid.NewGuid().ToString("N")[..8],
        GrantedPerformance = granted,
        EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-effectiveDaysAgo),
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(-effectiveDaysAgo + validityDays),
    };
}

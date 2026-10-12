using Autofac;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Completion;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Queries.Agents;
using MediatR;
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
        claim.Seal.ShouldNotBeNull().VerifierBundle.ShouldNotBeNull().Harness.ShouldBe("codex-cli", "the standing names WHO earned it — parsed back from the ledger row, not recomputed");
        claim.Cohort.ShouldNotBeNull().Tier.ShouldBe(LaunchCohortDescriptor.InternalQualificationTier);

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

    [Fact]
    public async Task Through_the_mediator_a_reader_outside_the_paying_team_never_sees_a_paying_teams_identifiers()
    {
        // Every row, not just one: whichever receipts the shared ledger holds, none of their paying teams or model
        // credentials may reach a reader whose team paid for none of them.
        var payingTeam = Guid.NewGuid();
        await SeedPaidReceiptAsync(payingTeam).ConfigureAwait(false);
        var (outsiderTeam, outsider) = await WorkflowsTestSeed.SeedTeamAsync(_fixture).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(outsider, outsiderTeam);
        var board = await scope.Resolve<IMediator>().Send(new GetQualificationClaimsQuery()).ConfigureAwait(false);

        board.Rows.ShouldAllBe(r => r.Cohort == null || r.Cohort.TeamId == null, "a paying team's id reaches only that team");
        board.Rows.ShouldAllBe(r => r.Seal == null || r.Seal.VerifierBundle == null || (r.Seal.VerifierBundle.ModelCredentialId == null && r.Seal.VerifierBundle.ModelCredentialModelId == null), "and so do its model-credential ids");
        board.Rows.ShouldContain(r => r.Mode == RunModeKeys.SingleAgent && r.CapabilityKey == CapabilityKeys.InlineAnswer && r.Performance == PerformanceQualification.Sealed, "the standing itself still reads");
    }

    [Fact]
    public async Task Through_the_mediator_the_paying_team_reads_its_own_receipt_whole()
    {
        var (payingTeam, payer) = await WorkflowsTestSeed.SeedTeamAsync(_fixture).ConfigureAwait(false);
        var receipt = await SeedPaidReceiptAsync(payingTeam).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(payer, payingTeam);
        var board = await scope.Resolve<IMediator>().Send(new GetQualificationClaimsQuery()).ConfigureAwait(false);

        var row = board.Rows.Single(r => r.Mode == RunModeKeys.SingleAgent && r.CapabilityKey == CapabilityKeys.InlineAnswer);
        row.ReceiptId.ShouldBe(receipt.Id, "the latest sealed receipt backs the pair");
        row.Cohort.ShouldNotBeNull().TeamId.ShouldBe(payingTeam);
        row.Seal.ShouldNotBeNull().VerifierBundle.ShouldNotBeNull().ModelCredentialId.ShouldNotBeNull();
    }

    /// <summary>Minted the way QualificationRunner mints a round: the cohort names the team that paid, and the verifier bundle names its model credential.</summary>
    private async Task<QualificationReceipt> SeedPaidReceiptAsync(Guid payingTeam)
    {
        using var scope = _fixture.BeginScope();
        var receipt = Receipt(RunModeKeys.SingleAgent, PerformanceQualification.Sealed);
        receipt.CapabilityKey = CapabilityKeys.InlineAnswer;
        receipt.VerifierBundleJson = $$"""{"harness":"codex-cli","model":"team-private-model","modelCredentialId":"{{Guid.NewGuid()}}","modelCredentialModelId":"{{Guid.NewGuid()}}"}""";
        receipt.CohortJson = $$"""{"teamId":"{{payingTeam}}","mode":"{{RunModeKeys.SingleAgent}}","tier":"internal-qualification","completionPolicyVersion":2}""";

        await scope.Resolve<IQualificationReceiptStore>().AppendAsync(receipt, CancellationToken.None).ConfigureAwait(false);

        return receipt;
    }

    private static string UniqueMode() => "claim-" + Guid.NewGuid().ToString("N")[..8];

    private static QualificationReceipt Receipt(string mode, PerformanceQualification granted, int effectiveDaysAgo = 0, int validityDays = 30) => new()
    {
        Id = Guid.NewGuid(),
        Mode = mode,
        CapabilityKey = "git-branch",
        SuiteDigest = "sha256:claim-suite-" + Guid.NewGuid().ToString("N")[..8],
        VerifierBundleJson = """{"harness":"codex-cli","model":"test-model","modelCredentialId":null}""",
        CohortJson = $$"""{"teamId":"{{Guid.NewGuid()}}","mode":"{{mode}}","tier":"internal-qualification","completionPolicyVersion":2}""",
        GrantedPerformance = granted,
        EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-effectiveDaysAgo),
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(-effectiveDaysAgo + validityDays),
    };
}

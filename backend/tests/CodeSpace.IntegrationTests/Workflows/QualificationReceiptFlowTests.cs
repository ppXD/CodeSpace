using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Completion;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres, the trigger under test): Q1's qualification-receipt ledger — a measured claim
/// is an IMMUTABLE row (the table's own trigger freezes every column at insert and refuses DELETE), the one
/// lawful mutation is the forward-only revoke, and the current-claim read honors the validity window + the
/// revocation without ever rewriting history.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class QualificationReceiptFlowTests
{
    private readonly PostgresFixture _fixture;

    public QualificationReceiptFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_receipt_backs_the_claim_only_inside_its_window_and_until_revoked()
    {
        using var scope = _fixture.BeginScope();
        var store = scope.Resolve<IQualificationReceiptStore>();
        var mode = "supervisor-" + Guid.NewGuid().ToString("N")[..6];

        var receipt = Receipt(mode, effectiveFrom: DateTimeOffset.UtcNow.AddDays(-1), expiresAt: DateTimeOffset.UtcNow.AddDays(30));
        await store.AppendAsync(receipt, CancellationToken.None);

        (await store.ListCurrentAsync(mode, "git-branch", DateTimeOffset.UtcNow, CancellationToken.None))
            .ShouldHaveSingleItem().GrantedPerformance.ShouldBe(PerformanceQualification.Sealed);

        (await store.ListCurrentAsync(mode, "git-branch", DateTimeOffset.UtcNow.AddDays(31), CancellationToken.None))
            .ShouldBeEmpty("a claim does not outlive its window — re-qualification mints a NEW receipt");

        (await store.RevokeAsync(receipt.Id, CancellationToken.None)).ShouldBeTrue();
        (await store.RevokeAsync(receipt.Id, CancellationToken.None)).ShouldBeFalse("the revoke is one-way and idempotent-by-refusal");

        (await store.ListCurrentAsync(mode, "git-branch", DateTimeOffset.UtcNow, CancellationToken.None))
            .ShouldBeEmpty("a revoked receipt backs no FUTURE gating");

        using var verify = _fixture.BeginScope();
        var row = await verify.Resolve<CodeSpaceDbContext>().QualificationReceipt.AsNoTracking().SingleAsync(r => r.Id == receipt.Id);
        row.GrantedPerformance.ShouldBe(PerformanceQualification.Sealed, "revocation never rewrites the claim's content — history intact");
        row.RevokedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_trigger_freezes_every_column_except_the_one_way_revoke()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var mode = "supervisor-" + Guid.NewGuid().ToString("N")[..6];

        var receipt = Receipt(mode, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        db.QualificationReceipt.Add(receipt);
        await db.SaveChangesAsync();

        // Tampering the granted tier is refused by the table itself — the claim is evidence, not state.
        var tamper = await db.Database.ExecuteSqlRawAsync(
            "UPDATE qualification_receipt SET granted_performance = 'Sealed', suite_digest = 'forged' WHERE id = {0}", receipt.Id)
            .ShouldThrowAsync<Exception>();
        tamper.Message.ShouldContain("immutable");

        var delete = await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM qualification_receipt WHERE id = {0}", receipt.Id).ShouldThrowAsync<Exception>();
        delete.Message.ShouldContain("immutable");
    }

    private static QualificationReceipt Receipt(string mode, DateTimeOffset effectiveFrom, DateTimeOffset expiresAt) => new()
    {
        Id = Guid.NewGuid(),
        Mode = mode,
        CapabilityKey = "git-branch",
        SuiteDigest = "sha256:suite-v1",
        VerifierBundleJson = """{"verifier":"hidden-suite/v1","model":"claude-sonnet","runner":"local"}""",
        CohortJson = """{"tier":"internal-canary"}""",
        GrantedPerformance = PerformanceQualification.Sealed,
        MetricsJson = """{"solveRate":0.62,"lowerBound":0.55}""",
        EffectiveFrom = effectiveFrom,
        ExpiresAt = expiresAt,
    };
}

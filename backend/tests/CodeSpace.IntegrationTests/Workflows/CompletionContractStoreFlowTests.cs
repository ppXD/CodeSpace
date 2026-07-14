using Autofac;
using CodeSpace.Core.Services.Completion;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Contracts;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres): the completion contract ledger's laws (P2a-2 / R) — requirement upsert
/// idempotency (one row per (run, kind, ref); amended envelope overwrites), receipt exactly-once under the
/// DISTINCT-target constraint (a crash-replayed append lands on the same row; duplicate targets collapse at the
/// DATABASE, not just at admission), and full-envelope round-trips (the jsonb is the truth).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CompletionContractStoreFlowTests
{
    private readonly PostgresFixture _fixture;

    public CompletionContractStoreFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Requirements_upsert_idempotently_and_amendments_overwrite()
    {
        var (teamId, _) = await Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = Guid.NewGuid();
        using var scope = _fixture.BeginScope();
        var store = scope.Resolve<ICompletionContractStore>();

        var requirement = Requirement("acceptance:s1", specHash: "sha256/canonical-json-v1:aaa");

        await store.UpsertRequirementsAsync(runId, teamId, new[] { requirement }, CancellationToken.None);
        await store.UpsertRequirementsAsync(runId, teamId, new[] { requirement }, CancellationToken.None);   // replay → no-op

        var afterReplay = await store.ListRequirementsAsync(runId, teamId, CancellationToken.None);
        afterReplay.ShouldHaveSingleItem().SpecHash.ShouldBe("sha256/canonical-json-v1:aaa");

        await store.UpsertRequirementsAsync(runId, teamId, new[] { requirement with { SpecHash = "sha256/canonical-json-v1:bbb" } }, CancellationToken.None);

        var afterAmend = await store.ListRequirementsAsync(runId, teamId, CancellationToken.None);
        afterAmend.ShouldHaveSingleItem().SpecHash.ShouldBe("sha256/canonical-json-v1:bbb", "an amended obligation overwrites its envelope — the ref is the identity");
    }

    [Fact]
    public async Task Receipts_append_exactly_once_per_attempt_and_target()
    {
        var (teamId, _) = await Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        using var scope = _fixture.BeginScope();
        var store = scope.Resolve<ICompletionContractStore>();

        var receipt = Receipt("acceptance:s1", attemptId, targetRef: "repo-A");

        await store.AppendReceiptAsync(runId, teamId, receipt, CancellationToken.None);
        await store.AppendReceiptAsync(runId, teamId, receipt, CancellationToken.None);   // crash replay → same row

        (await store.ListReceiptsAsync(runId, teamId, CancellationToken.None)).Count.ShouldBe(1, "exactly-once at the constraint, not just at admission");

        await store.AppendReceiptAsync(runId, teamId, receipt with { TargetRef = "repo-B" }, CancellationToken.None);
        await store.AppendReceiptAsync(runId, teamId, receipt with { AttemptId = Guid.NewGuid() }, CancellationToken.None);   // a NEW attempt's receipt is history, not a duplicate

        (await store.ListReceiptsAsync(runId, teamId, CancellationToken.None)).Count.ShouldBe(3);
    }

    [Fact]
    public async Task Envelopes_round_trip_byte_faithfully()
    {
        var (teamId, _) = await Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = Guid.NewGuid();
        using var scope = _fixture.BeginScope();
        var store = scope.Resolve<ICompletionContractStore>();

        var receipt = Receipt("acceptance:s1", Guid.NewGuid(), targetRef: null) with
        {
            WorkUnit = new WorkUnitRef { WorkPlanId = Guid.NewGuid(), PlanVersion = 2, UnitId = "s1", ContractHash = "sha256/canonical-json-v1:abc" },
            ContentHashes = new[] { "deadbeef" },
            EvaluatorVersion = "grader-v1",
        };

        await store.AppendReceiptAsync(runId, teamId, receipt, CancellationToken.None);

        var read = (await store.ListReceiptsAsync(runId, teamId, CancellationToken.None)).ShouldHaveSingleItem();
        read.WorkUnit!.ContractHash.ShouldBe("sha256/canonical-json-v1:abc");
        read.WorkUnit.PlanVersion.ShouldBe(2);
        read.ContentHashes.ShouldBe(new[] { "deadbeef" });
        read.Disposition.ShouldBe(VerificationDisposition.Passed);
    }

    private static RequirementEnvelope Requirement(string requirementRef, string specHash) => new()
    {
        RequirementRef = requirementRef, Kind = ContractKinds.Acceptance, Requiredness = Requiredness.Required,
        Authority = ContractAuthority.ModelProposal, SpecHash = specHash, ContractSchemaVersion = "1",
    };

    private static ReceiptEnvelope Receipt(string requirementRef, Guid attemptId, string? targetRef) => new()
    {
        RequirementRef = requirementRef, Kind = ContractKinds.Acceptance, AttemptId = attemptId, TargetRef = targetRef,
        Disposition = VerificationDisposition.Passed, Authority = ContractAuthority.ServerPolicy, ObservedAt = DateTimeOffset.UnixEpoch,
    };
}

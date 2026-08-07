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
    public async Task Amendments_append_to_the_revision_ledger_so_history_survives_the_overwrite()
    {
        // P1 (v4.3): the current row is a projection; the revision ledger is the history. Before this ledger, a
        // revised-instruction retry's re-stake destroyed the SpecHash the original attempt was staked under — the
        // exact value #1321 made admission's comparand.
        var (teamId, _) = await Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = Guid.NewGuid();
        using var scope = _fixture.BeginScope();
        var store = scope.Resolve<ICompletionContractStore>();

        var requirement = Requirement("acceptance:s1", specHash: "sha256/canonical-json-v1:aaa");

        await store.UpsertRequirementsAsync(runId, teamId, new[] { requirement }, CancellationToken.None);
        await store.UpsertRequirementsAsync(runId, teamId, new[] { requirement }, CancellationToken.None);   // replay — no amendment, no revision

        (await store.ListRequirementRevisionsAsync(runId, teamId, "acceptance:s1", ContractKinds.Acceptance, CancellationToken.None))
            .ShouldHaveSingleItem("the first stake is revision one; an identical replay appends nothing");

        await store.UpsertRequirementsAsync(runId, teamId, new[] { requirement with { SpecHash = "sha256/canonical-json-v1:bbb" } }, CancellationToken.None);

        var history = await store.ListRequirementRevisionsAsync(runId, teamId, "acceptance:s1", ContractKinds.Acceptance, CancellationToken.None);
        history.Count.ShouldBe(2, "an amendment overwrites the CURRENT row but APPENDS to the ledger");
        history[0].SpecHash.ShouldBe("sha256/canonical-json-v1:aaa", "oldest first — the shape the original attempt was staked under is still on record");
        history[1].SpecHash.ShouldBe("sha256/canonical-json-v1:bbb");

        (await store.ListRequirementsAsync(runId, teamId, CancellationToken.None))
            .ShouldHaveSingleItem().SpecHash.ShouldBe("sha256/canonical-json-v1:bbb", "the current projection is the ledger's newest entry");
    }

    [Fact]
    public async Task The_upsert_returns_the_current_revision_a_dispatcher_can_stamp()
    {
        // P1 (revision binding): a fresh stake returns its appended row's id; an idempotent replay returns the
        // SAME id (a crash-replayed staging stamps consistently); an amendment returns the newer one — and the
        // per-run view agrees with all three.
        var (teamId, _) = await Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = Guid.NewGuid();
        using var scope = _fixture.BeginScope();
        var store = scope.Resolve<ICompletionContractStore>();

        var requirement = Requirement("acceptance:s1", specHash: "sha256/canonical-json-v1:aaa");
        var key = ("acceptance:s1", ContractKinds.Acceptance);

        var first = await store.UpsertRequirementsAsync(runId, teamId, new[] { requirement }, CancellationToken.None);
        var replay = await store.UpsertRequirementsAsync(runId, teamId, new[] { requirement }, CancellationToken.None);

        replay[key].ShouldBe(first[key], "an idempotent replay resolves to the SAME revision — the reclaimed-orphan consistency the dispatch stamp relies on");

        var amended = await store.UpsertRequirementsAsync(runId, teamId, new[] { requirement with { SpecHash = "sha256/canonical-json-v1:bbb" } }, CancellationToken.None);

        amended[key].ShouldBeGreaterThan(first[key], "an amendment appends — the returned id is the newer row");
        (await store.GetCurrentRequirementRevisionsAsync(runId, teamId, CancellationToken.None))[key].ShouldBe(amended[key]);
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

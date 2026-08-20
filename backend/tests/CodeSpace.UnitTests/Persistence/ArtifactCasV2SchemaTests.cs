using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

/// <summary>
/// Pins Wave 3's provider-neutral artifact CAS schema before any runtime path may consume it. Global byte/storage
/// facts deliberately have no workflow prefix; only the run-owned semantic reference is workflow-scoped.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ArtifactCasV2SchemaTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Object_is_immutable_team_scoped_binary_content_identity()
    {
        using var db = BuildContext();
        var entity = Entity<ArtifactObject>(db);

        entity.GetTableName().ShouldBe("artifact_object");
        entity.GetProperties().Select(p => p.Name).Order().ShouldBe(new[]
        {
            "CreatedBy", "CreatedDate", "Digest", "DigestAlgorithm", "Id", "SizeBytes", "TeamId",
        }.Order());
        entity.FindProperty(nameof(ArtifactObject.Digest))!.GetColumnType().ShouldBe("bytea");
        entity.FindProperty(nameof(ArtifactObject.DigestAlgorithm))!.GetMaxLength().ShouldBe(16);

        var digest = Index(entity, "ux_artifact_object_digest");
        digest.IsUnique.ShouldBeTrue();
        digest.Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "DigestAlgorithm", "Digest" });

        AlternateKey(entity, "ak_artifact_object_team_id").Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "Id" });
        entity.GetCheckConstraints().Select(c => c.Name).ShouldBe(new[]
        {
            "ck_artifact_object_digest", "ck_artifact_object_size",
        }, ignoreOrder: true);
    }

    [Fact]
    public void Location_binds_exact_object_and_profile_revision_and_keeps_provider_observation_fields_typed()
    {
        using var db = BuildContext();
        var entity = Entity<ArtifactLocation>(db);

        entity.GetTableName().ShouldBe("artifact_location");
        entity.GetProperties().Select(p => p.Name).Order().ShouldBe(new[]
        {
            "ArtifactObjectId", "ContentEncoding", "CreatedBy", "CreatedDate", "EncryptionKeyVersion", "Id",
            "LastErrorCode", "LastErrorMessage", "LastModifiedBy", "LastModifiedDate", "Locator", "ObjectKey",
            "ObservedSizeBytes", "ProviderChecksum", "ProviderChecksumAlgorithm", "ProviderETag",
            "ProviderObjectVersion", "Revision", "State", "StorageProfileRevisionId", "TeamId", "VerifiedAt", "Xmin",
        }.Order());
        entity.FindProperty(nameof(ArtifactLocation.ProviderChecksum))!.GetColumnType().ShouldBe("bytea");
        entity.FindProperty(nameof(ArtifactLocation.State))!.GetMaxLength().ShouldBe(24);
        entity.FindProperty(nameof(ArtifactLocation.Xmin))!.IsConcurrencyToken.ShouldBeTrue();
        CheckConstraint(entity, "ck_artifact_location_checksum").Sql.ShouldContain("(provider_checksum_algorithm IS NULL) = (provider_checksum IS NULL)");
        CheckConstraint(entity, "ck_artifact_location_observation").Sql.ShouldContain("provider_checksum_algorithm = 'Sha256'");
        CheckConstraint(entity, "ck_artifact_location_state").Sql.ShouldContain("'Purged'",
            customMessage: "a purge needs a non-terminal state to leave behind; 'Deleted' is terminal by trigger and makes its content unstorable");
        AlternateKey(entity, "ak_artifact_location_team_id").Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "Id" });

        ForeignKey(entity, typeof(ArtifactObject)).Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "ArtifactObjectId" });
        ForeignKey(entity, typeof(StorageProfileRevision)).Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "StorageProfileRevisionId" });
        ForeignKey(entity, typeof(StorageProfileRevision)).PrincipalKey.Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "Id" });

        Index(entity, "ux_artifact_location_profile_object_key").IsUnique.ShouldBeTrue();
        entity.GetCheckConstraints().Select(c => c.Name).ShouldBe(new[]
        {
            "ck_artifact_location_checksum", "ck_artifact_location_encoding", "ck_artifact_location_error",
            "ck_artifact_location_identity", "ck_artifact_location_observation", "ck_artifact_location_revision",
            "ck_artifact_location_state",
        }, ignoreOrder: true);
    }

    [Fact]
    public void Location_event_is_an_append_only_tenant_bound_revision_history()
    {
        using var db = BuildContext();
        var entity = Entity<ArtifactLocationEvent>(db);

        entity.GetTableName().ShouldBe("artifact_location_event");
        entity.GetProperties().Select(p => p.Name).Order().ShouldBe(new[]
        {
            "ArtifactLocationId", "ContentEncoding", "CreatedBy", "DetailsJson", "EncryptionKeyVersion", "ErrorCode",
            "ErrorMessage", "EventType", "Id", "ObservedAt", "ObservedSizeBytes", "ProviderChecksum",
            "ProviderChecksumAlgorithm", "ProviderETag", "ProviderObjectVersion", "Revision", "State", "TeamId",
            "VerifiedAt",
        }.Order());
        entity.FindProperty(nameof(ArtifactLocationEvent.DetailsJson))!.GetColumnType().ShouldBe("jsonb");
        ForeignKey(entity, typeof(ArtifactLocation)).Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "ArtifactLocationId" });
        CheckConstraint(entity, "ck_artifact_location_event_checksum").Sql.ShouldContain("(provider_checksum_algorithm IS NULL) = (provider_checksum IS NULL)");
        CheckConstraint(entity, "ck_artifact_location_event_state").Sql.ShouldContain("'Purged'", customMessage: "the append-only history has to be able to record the state the location can reach");

        var revision = Index(entity, "ux_artifact_location_event_revision");
        revision.IsUnique.ShouldBeTrue();
        revision.Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "ArtifactLocationId", "Revision" });
        entity.GetCheckConstraints().Select(c => c.Name).ShouldBe(new[]
        {
            "ck_artifact_location_event_checksum", "ck_artifact_location_event_details",
            "ck_artifact_location_event_error", "ck_artifact_location_event_revision",
            "ck_artifact_location_event_type", "ck_artifact_location_event_state",
        }, ignoreOrder: true);
    }

    [Fact]
    public void Transfer_intent_is_idempotent_fenced_and_links_only_tenant_bound_results()
    {
        using var db = BuildContext();
        var entity = Entity<ArtifactTransferIntent>(db);

        entity.GetTableName().ShouldBe("artifact_transfer_intent");
        entity.GetProperties().Select(p => p.Name).Order().ShouldBe(new[]
        {
            "ArtifactLocationId", "ArtifactObjectId", "CompletedAt", "CreatedBy", "CreatedDate", "ExecutionAttemptId",
            "ExecutionAttemptOrdinal", "ExecutionGeneration", "ExpectedDigest", "ExpectedDigestAlgorithm",
            "ExpectedSizeBytes", "Id", "IdempotencyKey", "LastErrorCode", "LastErrorMessage", "LastModifiedBy",
            "LastModifiedDate", "NextAttemptAt", "ProviderUploadId", "RetryCount", "Revision", "State",
            "StorageProfileRevisionId", "TargetLocator", "TargetObjectKey", "TeamId", "TemporaryObjectKey",
            "WorkerFenceEpoch", "WorkerLeaseExpiresAt", "Xmin",
        }.Order());
        entity.FindProperty(nameof(ArtifactTransferIntent.ExpectedDigest))!.GetColumnType().ShouldBe("bytea");
        entity.FindProperty(nameof(ArtifactTransferIntent.Xmin))!.IsConcurrencyToken.ShouldBeTrue();
        CheckConstraint(entity, "ck_artifact_transfer_intent_attempt").Sql.ShouldContain("execution_attempt_ordinal IS NOT NULL");
        CheckConstraint(entity, "ck_artifact_transfer_intent_attempt").Sql.ShouldContain("worker_fence_epoch IS NULL OR worker_fence_epoch > 0");
        CheckConstraint(entity, "ck_artifact_transfer_intent_worker_lease").Sql.ShouldContain("worker_lease_expires_at IS NULL OR worker_fence_epoch IS NOT NULL");
        ForeignKey(entity, typeof(StorageProfileRevision)).Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "StorageProfileRevisionId" });
        ForeignKey(entity, typeof(ArtifactObject)).Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "ArtifactObjectId" });
        ForeignKey(entity, typeof(ArtifactLocation)).Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "ArtifactLocationId" });

        var idempotency = Index(entity, "ux_artifact_transfer_intent_idempotency");
        idempotency.IsUnique.ShouldBeTrue();
        idempotency.Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "StorageProfileRevisionId", "IdempotencyKey" });
        entity.GetCheckConstraints().Select(c => c.Name).ShouldBe(new[]
        {
            "ck_artifact_transfer_intent_attempt", "ck_artifact_transfer_intent_digest",
            "ck_artifact_transfer_intent_error", "ck_artifact_transfer_intent_identity",
            "ck_artifact_transfer_intent_outcome", "ck_artifact_transfer_intent_retry",
            "ck_artifact_transfer_intent_revision", "ck_artifact_transfer_intent_state",
            "ck_artifact_transfer_intent_worker_lease",
        }, ignoreOrder: true);
    }

    [Fact]
    public void Run_reference_is_the_only_workflow_scoped_table_and_binds_lineage_to_exact_object()
    {
        using var db = BuildContext();
        var entity = Entity<WorkflowRunArtifactReference>(db);

        entity.GetTableName().ShouldBe("workflow_run_artifact_reference");
        entity.GetProperties().Select(p => p.Name).Order().ShouldBe(new[]
        {
            "ArtifactObjectId", "ContentType", "CreatedBy", "CreatedDate", "ExecutionAttemptId",
            "ExecutionAttemptOrdinal", "ExecutionGeneration", "ExpiresAt", "Id", "IterationKey", "LogicalPath",
            "NodeId", "PlanVersion", "Required", "RequirementRevision", "Retention", "Role",
            "SupersededByReferenceId", "TeamId", "WorkflowRunId", "WorkPlanId", "WorkUnitContractHash", "WorkUnitId",
        }.Order());
        entity.FindProperty(nameof(WorkflowRunArtifactReference.Role))!.GetMaxLength().ShouldBe(128);
        entity.FindProperty(nameof(WorkflowRunArtifactReference.ContentType))!.GetMaxLength().ShouldBe(255);
        CheckConstraint(entity, "ck_run_artifact_reference_attempt").Sql.ShouldContain("execution_generation IS NOT NULL");
        CheckConstraint(entity, "ck_run_artifact_reference_work_unit").Sql.ShouldContain("plan_version IS NOT NULL");
        ForeignKey(entity, typeof(ArtifactObject)).Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "ArtifactObjectId" });
        ForeignKey(entity, typeof(WorkflowRun)).Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "WorkflowRunId" });
        ForeignKey(entity, typeof(WorkPlan)).Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "WorkPlanId", "WorkflowRunId", "PlanVersion" });
        ForeignKey(entity, typeof(WorkflowRunArtifactReference)).Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "SupersededByReferenceId" });

        Index(entity, "ix_run_artifact_reference_active").GetFilter().ShouldBe("superseded_by_reference_id IS NULL");
        var attemptPath = Index(entity, "ux_run_artifact_reference_attempt_path");
        attemptPath.IsUnique.ShouldBeTrue();
        attemptPath.Properties.Select(p => p.Name).ShouldBe(new[]
        {
            "TeamId", "WorkflowRunId", "ExecutionAttemptId", "ExecutionGeneration", "Role", "LogicalPath",
        });
        attemptPath.GetFilter().ShouldBe("execution_attempt_id IS NOT NULL AND superseded_by_reference_id IS NULL");
        entity.GetCheckConstraints().Select(c => c.Name).ShouldBe(new[]
        {
            "ck_run_artifact_reference_attempt", "ck_run_artifact_reference_content_type",
            "ck_run_artifact_reference_expiry", "ck_run_artifact_reference_path",
            "ck_run_artifact_reference_retention", "ck_run_artifact_reference_role",
            "ck_run_artifact_reference_superseded", "ck_run_artifact_reference_work_unit",
        }, ignoreOrder: true);
    }

    private static CodeSpaceDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>()
            .UseNpgsql(UnreachableDatabase)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new CodeSpaceDbContext(options);
    }

    private static IEntityType Entity<TEntity>(CodeSpaceDbContext db) => db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(TEntity)).ShouldNotBeNull();

    private static IIndex Index(IEntityType entity, string name) => entity.GetIndexes().Single(i => i.GetDatabaseName() == name);

    private static IKey AlternateKey(IEntityType entity, string name) => entity.GetKeys().Single(k => k.GetName() == name);

    private static ICheckConstraint CheckConstraint(IEntityType entity, string name) => entity.GetCheckConstraints().Single(c => c.Name == name);

    private static IForeignKey ForeignKey(IEntityType entity, Type principal) => entity.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == principal);
}

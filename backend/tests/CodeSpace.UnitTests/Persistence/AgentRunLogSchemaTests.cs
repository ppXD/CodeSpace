using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

/// <summary>
/// Pins the provider- and harness-neutral Agent Run log archive. Agent runs may be standalone, so these tables are
/// correctly agent_run-owned rather than workflow_run-prefixed; payload bytes live in artifact CAS, never PostgreSQL.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentRunLogSchemaTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Stream_is_a_tenant_bound_monotonic_head_for_one_open_versioned_log_kind()
    {
        using var db = BuildContext();
        var entity = Entity<AgentRunLogStream>(db);

        entity.GetTableName().ShouldBe("agent_run_log_stream");
        entity.GetProperties().Select(property => property.Name).Order().ShouldBe(new[]
        {
            "AgentRunId", "CaptureFinalizedAt", "CaptureSessionId", "CaptureSource", "CaptureSourceBaseOffsetBytes", "CompletedAt", "ContentDigest", "ContentDigestAlgorithm",
            "ContentEncoding", "ContentType", "CreatedAt", "ErrorCode", "ErrorMessage", "ExpiresAt", "Id", "LastModifiedAt",
            "NextOffsetBytes", "NextSegmentOrdinal", "Retention", "Revision", "SchemaVersion", "SegmentCount", "State",
            "SourceOffsetBytes", "StreamKind", "TeamId", "TotalBytes", "WorkerFenceEpoch", "Xmin",
        }.Order());
        entity.FindProperty(nameof(AgentRunLogStream.State))!.GetMaxLength().ShouldBe(24);
        entity.FindProperty(nameof(AgentRunLogStream.Xmin))!.IsConcurrencyToken.ShouldBeTrue();
        AlternateKey(entity, "ak_agent_run_log_stream_scope").Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "Id", "AgentRunId" });
        ForeignKey(entity, typeof(AgentRun)).Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "AgentRunId" });

        var identity = Index(entity, "ux_agent_run_log_stream_kind");
        identity.IsUnique.ShouldBeTrue();
        identity.Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "AgentRunId", "StreamKind" });
        entity.GetCheckConstraints().Select(constraint => constraint.Name).ShouldBe(new[]
        {
            "ck_agent_run_log_stream_claim", "ck_agent_run_log_stream_digest", "ck_agent_run_log_stream_error", "ck_agent_run_log_stream_head", "ck_agent_run_log_stream_identity",
            "ck_agent_run_log_stream_retention", "ck_agent_run_log_stream_state", "ck_agent_run_log_stream_terminal",
            "ck_agent_run_log_stream_time",
        }, ignoreOrder: true);
    }

    [Fact]
    public void Capture_session_is_append_preserved_per_spool_progress_and_health()
    {
        using var db = BuildContext();
        var entity = Entity<AgentRunLogCaptureSession>(db);

        entity.GetTableName().ShouldBe("agent_run_log_capture_session");
        entity.GetProperties().Select(property => property.Name).Order().ShouldBe(new[]
        {
            "AgentRunId", "CaptureSessionId", "CreatedAt", "CurrentWorkerFenceEpoch", "ErrorCode", "ErrorMessage",
            "FinalizedAt", "Id", "InitialWorkerFenceEpoch", "LastObservedAt", "Revision", "SourceBaseOffsetBytes",
            "SourceOffsetBytes", "State", "StreamId", "TeamId", "Xmin",
        }.Order());
        entity.FindProperty(nameof(AgentRunLogCaptureSession.Xmin))!.IsConcurrencyToken.ShouldBeTrue();
        AlternateKey(entity, "ak_agent_run_log_capture_session_identity").Properties.Select(property => property.Name)
            .ShouldBe(new[] { "TeamId", "StreamId", "CaptureSessionId" });
        ForeignKey(entity, typeof(AgentRunLogStream)).Properties.Select(property => property.Name)
            .ShouldBe(new[] { "TeamId", "StreamId", "AgentRunId" });
        entity.GetCheckConstraints().Select(constraint => constraint.Name).ShouldBe(new[]
        {
            "ck_agent_run_log_capture_session_bounds", "ck_agent_run_log_capture_session_identity",
            "ck_agent_run_log_capture_session_state", "ck_agent_run_log_capture_session_time",
        }, ignoreOrder: true);
    }

    [Fact]
    public void Segment_is_append_only_byte_addressed_fenced_and_contains_only_a_cas_reference()
    {
        using var db = BuildContext();
        var entity = Entity<AgentRunLogSegment>(db);

        entity.GetTableName().ShouldBe("agent_run_log_segment");
        entity.GetProperties().Select(property => property.Name).Order().ShouldBe(new[]
        {
            "AgentRunId", "ArtifactObjectId", "CaptureSessionId", "CreatedAt", "FirstObservedAt", "Id",
            "LastObservedAt", "LengthBytes", "SchemaVersion", "SegmentOrdinal", "StartOffsetBytes", "StreamId",
            "SourceLengthBytes", "SourceStartOffsetBytes", "TeamId", "WorkerFenceEpoch",
        }.Order());
        ForeignKey(entity, typeof(AgentRunLogStream)).Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "StreamId", "AgentRunId" });
        ForeignKey(entity, typeof(AgentRunLogCaptureSession)).Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "StreamId", "CaptureSessionId" });
        ForeignKey(entity, typeof(ArtifactObject)).Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "ArtifactObjectId" });

        var ordinal = Index(entity, "ux_agent_run_log_segment_ordinal");
        ordinal.IsUnique.ShouldBeTrue();
        ordinal.Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "StreamId", "SegmentOrdinal" });
        var offset = Index(entity, "ux_agent_run_log_segment_offset");
        offset.IsUnique.ShouldBeTrue();
        offset.Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "StreamId", "StartOffsetBytes" });
        entity.GetCheckConstraints().Select(constraint => constraint.Name).ShouldBe(new[]
        {
            "ck_agent_run_log_segment_bounds", "ck_agent_run_log_segment_identity", "ck_agent_run_log_segment_observation",
        }, ignoreOrder: true);
    }

    private static CodeSpaceDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(UnreachableDatabase).UseSnakeCaseNamingConvention().Options;
        return new CodeSpaceDbContext(options);
    }

    private static IEntityType Entity<TEntity>(CodeSpaceDbContext db) => db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(TEntity)).ShouldNotBeNull();
    private static IIndex Index(IEntityType entity, string name) => entity.GetIndexes().Single(index => index.GetDatabaseName() == name);
    private static IKey AlternateKey(IEntityType entity, string name) => entity.GetKeys().Single(key => key.GetName() == name);
    private static IForeignKey ForeignKey(IEntityType entity, Type principal) => entity.GetForeignKeys().Single(key => key.PrincipalEntityType.ClrType == principal);
}

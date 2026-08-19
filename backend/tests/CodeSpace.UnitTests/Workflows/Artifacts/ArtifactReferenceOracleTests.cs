using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts;

/// <summary>
/// The oracle's site list IS the reaper's correctness: a referencing column missing from it makes the oracle answer
/// "unreferenced" about an artifact that is referenced, which is the one failure mode that destroys data. So the list is
/// pinned literally AND cross-checked against the EF model, so a NEW soft link added to any entity fails this class
/// rather than silently widening what the reaper is allowed to delete.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ArtifactReferenceOracleTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1;Command Timeout=1";

    [Fact]
    public void Every_soft_link_to_an_artifact_is_a_probed_site()
    {
        ArtifactReferenceOracle.ReferenceSites.ShouldBe(new[]
        {
            ("artifact_manifest", "content_artifact_id"),
            ("publish_manifest", "patch_artifact_id"),
            ("agent_run_event", "data_artifact_id"),
            ("workflow_run_model_call", "request_artifact_id"),
            ("workflow_run_model_call_attempt", "request_artifact_id"),
            ("workflow_run_model_call_attempt", "response_artifact_id"),
            ("workflow_run_model_call_attempt", "error_artifact_id"),
            ("workflow_run_tool_call", "arguments_artifact_id"),
            ("workflow_run_tool_call_attempt", "result_artifact_id"),
            ("workflow_run_tool_call_attempt", "error_artifact_id"),
        }, ignoreOrder: true);
    }

    [Fact]
    public void A_mapped_column_that_names_an_artifact_and_is_not_probed_fails_this_test()
    {
        // The drift detector. No foreign key points at workflow_artifact anywhere in this schema, so the ONLY machine
        // signal that a column is an artifact soft link is its name. Any future `*_artifact_id` column that is not
        // ArtifactObject-plane and not in ReferenceSites shows up here.
        using var db = BuildContext();
        var probed = ArtifactReferenceOracle.ReferenceSites.Select(site => $"{site.Table}.{site.Column}").ToHashSet(StringComparer.Ordinal);

        var mapped = db.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties().Select(property => (Table: entity.GetTableName(), Column: ColumnNameOf(entity, property))))
            .Where(column => column.Column is not null && IsArtifactSoftLink(column.Table, column.Column!))
            .Select(column => $"{column.Table}.{column.Column}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        mapped.Where(column => !probed.Contains(column)).ShouldBeEmpty(
            "a soft link to workflow_artifact that the oracle does not probe would let the reaper delete a referenced object — add it to ArtifactReferenceOracle.ReferenceSites and give it an index");
    }

    [Fact]
    public async Task An_unreadable_reference_site_reads_as_indeterminate_never_as_unreferenced()
    {
        // Fail-closed at the boundary: the probe cannot even reach a server here, so the question is UNANSWERED.
        // Answering "unreferenced" would delete an object whose references were never inspected.
        using var db = BuildContext();

        var verdict = await new ArtifactReferenceOracle(NullLogger<ArtifactReferenceOracle>.Instance).ClassifyAsync(db, Guid.NewGuid(), CancellationToken.None);

        verdict.ShouldBe(ArtifactReferenceVerdict.Indeterminate);
    }

    /// <summary>
    /// A column that soft-links <c>workflow_artifact.id</c>: named <c>*_artifact_id</c>, excluding the CAS v2 object
    /// plane (<c>*_artifact_object_id</c>), which is a different target carrying real foreign keys. The bare
    /// <c>artifact_id</c> is excluded because the only column with that name is the retention ledger's own primary
    /// key — the declaration itself, not a reference that would keep the artifact alive.
    /// </summary>
    /// <summary>
    /// Whether this (table, column) is a soft link the oracle must probe. The exclusion is scoped to the retention
    /// ledger's own PRIMARY KEY, not to the bare column name: excluding "artifact_id" everywhere would silently wave
    /// through a future reference column that happens to be named exactly that — and a reference the oracle never
    /// probes is a reference the reaper cannot see, which is the one failure mode this detector exists to prevent.
    /// </summary>
    private static bool IsArtifactSoftLink(string table, string column) =>
        column.EndsWith("artifact_id", StringComparison.Ordinal)
        && !column.EndsWith("artifact_object_id", StringComparison.Ordinal)
        && !string.Equals($"{table}.{column}", "workflow_artifact_retention.artifact_id", StringComparison.Ordinal);

    private static string? ColumnNameOf(IEntityType entity, IProperty property) =>
        entity.GetTableName() is { } table ? property.GetColumnName(StoreObjectIdentifier.Table(table, entity.GetSchema())) : null;

    private static CodeSpaceDbContext BuildContext() =>
        new(new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(UnreachableDatabase).UseSnakeCaseNamingConvention().Options);
}

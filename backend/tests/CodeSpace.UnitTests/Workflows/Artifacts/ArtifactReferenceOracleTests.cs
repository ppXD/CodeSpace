using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Retention;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
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
            ("workflow_run_model_call_body_capture", "artifact_id"),
            ("workflow_run_tool_call", "arguments_artifact_id"),
            ("workflow_run_tool_call_attempt", "result_artifact_id"),
            ("workflow_run_tool_call_attempt", "error_artifact_id"),
            ("workflow_run_sensitive_record_payload", "ciphertext_artifact_id"),
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
    public void Legacy_adoption_copies_source_identity_without_creating_a_retention_reference()
    {
        using var db = BuildContext();
        var arc = db.Model.FindEntityType(typeof(LegacyPlacementAdoptionArc)).ShouldNotBeNull();
        var member = db.Model.FindEntityType(typeof(LegacyPlacementAdoptionMember)).ShouldNotBeNull();
        var arcColumn = ColumnNameOf(arc, arc.FindProperty(nameof(LegacyPlacementAdoptionArc.WitnessSourceWorkflowRowId)).ShouldNotBeNull());
        var memberColumn = ColumnNameOf(member, member.FindProperty(nameof(LegacyPlacementAdoptionMember.SourceWorkflowRowId)).ShouldNotBeNull());

        arcColumn.ShouldBe("witness_source_workflow_row_id");
        memberColumn.ShouldBe("source_workflow_row_id");
        IsArtifactSoftLink(arc.GetTableName()!, arcColumn!).ShouldBeFalse("a copied witness identity must not tell retention to keep its source row alive");
        IsArtifactSoftLink(member.GetTableName()!, memberColumn!).ShouldBeFalse("sealed membership survives source retention and therefore is provenance, not a soft link");
        new[] { arc, member }.SelectMany(entity => entity.GetForeignKeys())
            .ShouldAllBe(foreignKey => foreignKey.PrincipalEntityType.ClrType != typeof(WorkflowArtifact),
                "legacy adoption exact-revalidates sources at commit but deliberately has no runtime or retention FK to workflow_artifact");
        ArtifactReferenceOracle.ReferenceSites.ShouldNotContain(site => site.Table.StartsWith("legacy_placement_adoption_", StringComparison.Ordinal),
            "the oracle must not keep a source alive merely because an unfinished or terminal adoption arc copied its identity");
    }

    [Fact]
    public void Legacy_adoption_migration_declares_copied_source_rows_without_a_workflow_artifact_foreign_key()
    {
        var migration = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles", "0186_legacy_placement_adoption_arc.sql"));

        migration.ShouldContain("witness_source_workflow_row_id");
        migration.ShouldContain("source_workflow_row_id");
        migration.ShouldNotContain("witness_workflow_artifact_id");
        migration.ShouldNotContain("workflow_artifact_id");
        migration.ShouldNotContain("REFERENCES workflow_artifact",
            customMessage: "a manifest row is copied provenance that retention may outlive, never a database reference keeping its source alive");
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
    /// Artifact-id-valued fields whose NAME does not contain "artifact", listed so that promoting one to a database
    /// column is a decision rather than an accident.
    ///
    /// <para><see cref="IsArtifactSoftLink"/> recognises a reference by its column name ending in
    /// <c>artifact_id</c> — its own comment concedes that is the only machine signal available, since no foreign key
    /// in this schema points at <c>workflow_artifact</c>. These three carry an artifact id under a name that signal
    /// cannot see. They live in JSON today, where the oracle already does not look, so nothing is wrong right now.
    /// The hazard is the promotion: the model-call lane already turned a JSON <c>$artifact_id</c> into four real
    /// columns, and if one of these followed that path the drift detector would wave it through in silence — an
    /// artifact the reaper deletes while a live column still names it.</para>
    /// </summary>
    private static readonly IReadOnlyList<(Type Owner, string Property)> UnnamedArtifactReferences =
    [
        (typeof(AgentRunResult), nameof(AgentRunResult.PublishEvidenceId)),
        (typeof(AgentRunResult), nameof(AgentRunResult.AcceptanceEvidenceId)),
        (typeof(RepositoryRunResult), nameof(RepositoryRunResult.PublishEvidenceId)),
        (typeof(SupervisorAgentResult), nameof(SupervisorAgentResult.AcceptanceEvidenceId)),
        (typeof(ReceiptEnvelope), nameof(ReceiptEnvelope.EvidenceRef)),
    ];

    [Fact]
    public void An_artifact_reference_whose_name_hides_it_is_not_silently_promoted_to_a_column()
    {
        // The detector's blind spot, made loud. Each of these holds a workflow_artifact id under a name containing
        // neither "artifact" nor "_id" in the shape the detector matches. While they live only in JSON the oracle is
        // honest — it never claims to read JSON. The moment one becomes a column it becomes a reference the oracle
        // does not probe, and the reaper deletes artifacts that column still names.
        var mapped = BuildContext().Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties().Select(property => ColumnNameOf(entity, property)))
            .Where(column => column != null).Select(column => column!).ToHashSet(StringComparer.Ordinal);

        foreach (var (owner, property) in UnnamedArtifactReferences)
        {
            owner.GetProperty(property).ShouldNotBeNull($"{owner.Name}.{property} was renamed or removed — update this list rather than deleting the check");
            mapped.ShouldNotContain(SnakeCase(property),
                $"{owner.Name}.{property} carries a workflow_artifact id and is now a mapped column, but IsArtifactSoftLink cannot see that name. "
                + "Add it to ArtifactReferenceOracle.ReferenceSites and to the detector, or the reaper will delete artifacts it still names.");
        }
    }

    private static string SnakeCase(string property) =>
        string.Concat(property.Select((character, index) => char.IsUpper(character) && index > 0 ? $"_{char.ToLowerInvariant(character)}" : $"{char.ToLowerInvariant(character)}"));

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

using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Retention.Cursors;
using CodeSpace.Messages.Agents.Recovery;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Retention;

/// <summary>
/// What may still cite a settled cleanup receipt, which of its outcomes a sweep may touch at all, and whether the
/// cursor's raw SQL says the same thing as its C#. The list is the correctness of the cursor, exactly as
/// <c>ArtifactReferenceOracle.ReferenceSites</c> is for artifacts: a citer missing from it makes the cursor delete a
/// row something still points at.
///
/// <para>Three checks keep the list from being decoration — it is compared against the EF model (a new column that
/// names a receipt reds even though nobody thought about retention while adding it), against the cursor's own
/// classification body (an entry with no probe beside it reds), and against the SQL the claim actually runs (two
/// copies of one rule drift unobserved).</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class RecordCitationSitesTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Every_citer_of_a_settled_cleanup_receipt_is_pinned()
    {
        CleanupReceiptRetentionCursor.CitationSites.ShouldBe(new[]
        {
            // A sealed qualification result still cites the receipt as part of the evidence it was computed from.
            // Nothing else names a receipt by id: the Room reads them by RUN and folds them, and the orphan sweep
            // selects only Orphaned rows, which this cursor never claims.
            ("paired_qualification_result_pin", "pinned_id"),
        }, ignoreOrder: true);
    }

    /// <summary>
    /// The outcomes a sweep may reclaim, held against the ledger's OWN definition of settled rather than a second
    /// opinion about it. Orphaned is addressed to a sweep on the host that owes the work and can still become
    /// Compensated. Unknown is rewritable — the ledger's upsert fences only the two settled outcomes, so a live leak
    /// can move Orphaned → Unknown — and it is READ by the Room's recovery card with no age bound at all.
    /// </summary>
    [Fact]
    public void A_sweep_may_reclaim_exactly_the_outcomes_the_ledger_itself_calls_settled()
    {
        CleanupReceiptRetentionCursor.SettledOutcomes.ShouldBe([RunResourceOutcome.Completed, RunResourceOutcome.Compensated], ignoreOrder: true);

        foreach (var outcome in Enum.GetValues<RunResourceOutcome>())
            CleanupReceiptRetentionCursor.SettledOutcomes.Contains(outcome).ShouldBe(Receipt(outcome).IsSettled,
                $"'{outcome}' disagrees with RunCleanupReceipt.IsSettled — the plane that WRITES receipts already says which ones are over");
    }

    [Fact]
    public void Every_pinned_citation_site_is_actually_probed_by_the_cursor()
    {
        using var db = BuildContext();

        // ClassifyAsync's own body, never the whole file: the deleting statement repeats the same probe, so a wider
        // match would keep passing with the classification's probe removed — and the classification is what the loop
        // asks before anything is deleted.
        var probes = ExistenceQuestions(MethodBody(Source(), "ClassifyAsync"));
        probes.ShouldNotBeNullOrWhiteSpace("the classification asks no existence question at all, so this check would pass by reading nothing");

        var unprobed = CleanupReceiptRetentionCursor.CitationSites
            .Select(site => (site.Table, site.Column, Member: MemberOf(db, site.Table, site.Column)))
            .Where(site => !probes.Contains($".{site.Member}", StringComparison.Ordinal))
            .Select(site => $"{site.Table}.{site.Column} (no use of .{site.Member} in the citation probe)")
            .ToList();

        unprobed.ShouldBeEmpty("a site the cursor lists but never asks about is a claim it does not keep:\n  " + string.Join("\n  ", unprobed));
    }

    /// <summary>
    /// The drift detector, over the EF model rather than the list: a future column that names a cleanup receipt reds
    /// here even though nobody thought about retention while adding it.
    /// </summary>
    [Fact]
    public void A_mapped_column_that_names_a_cleanup_receipt_and_is_not_probed_fails_this_test()
    {
        using var db = BuildContext();
        var probed = CleanupReceiptRetentionCursor.CitationSites.Select(site => $"{site.Table}.{site.Column}").ToHashSet(StringComparer.Ordinal);

        var mapped = db.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties().Select(property => (Table: entity.GetTableName(), Column: property.GetColumnName())))
            .Where(column => column.Table is not null && column.Column is not null && NamesACleanupReceipt(column.Table!, column.Column!))
            .Select(column => $"{column.Table}.{column.Column}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        mapped.Where(column => !probed.Contains(column)).ShouldBeEmpty(
            $"a column that names a cleanup receipt which {nameof(CleanupReceiptRetentionCursor)} does not probe would let it delete a row that column still points at — "
            + $"add it to {nameof(CleanupReceiptRetentionCursor.CitationSites)} and to the probe beside it");
    }

    /// <summary>
    /// The claim's raw SQL and the cursor's C# allow-lists are two copies of one rule, and two copies drift
    /// unobserved. The claim cannot parameterise an <c>IN</c> list of enum names, so this reads the cursor's own
    /// source and compares what its SQL says with what its C# says.
    /// </summary>
    [Fact]
    public void The_claims_raw_sql_says_exactly_what_the_allow_lists_say()
    {
        var source = Source();
        var expected = string.Join(", ", CleanupReceiptRetentionCursor.SettledOutcomes.Select(outcome => $"'{outcome}'"));

        CleanupReceiptRetentionCursor.SettledOutcomeNames.ShouldBe(expected, "the SQL literal and the enum allow-list are one rule written twice");
        CleanupReceiptRetentionCursor.PinnedKindName.ShouldBe(DurablePinKind.CleanupReceipt.ToString());

        var inLists = System.Text.RegularExpressions.Regex.Matches(source, @"outcome IN \(([^)]*)\)").Select(match => match.Groups[1].Value.Trim()).ToList();
        var pinKinds = System.Text.RegularExpressions.Regex.Matches(source, @"pin\.kind = '([^']*)'").Select(match => match.Groups[1].Value).ToList();

        inLists.ShouldNotBeEmpty("the claim's outcome filter was not found, so this check would pass by reading nothing");
        pinKinds.ShouldNotBeEmpty("the claim's pin filter was not found, so this check would pass by reading nothing");
        inLists.ShouldAllBe(list => list == CleanupReceiptRetentionCursor.SettledOutcomeNames, "every outcome list in the cursor's SQL is the settled set, exactly");
        pinKinds.ShouldAllBe(kind => kind == CleanupReceiptRetentionCursor.PinnedKindName, "the pin kind the SQL names is the one the enum spells");
    }

    /// <summary>
    /// A column names a cleanup receipt when it ends in <c>cleanup_receipt_id</c>, or when it is the generic pin
    /// column the retention plane uses. Near misses, stated rather than left to a reader to wonder about: the
    /// receipt's own <c>agent_run_id</c> and <c>team_id</c> point the other way — the receipt names THEM — and
    /// <c>workflow_run_model_call_attempt.budget_reservation_id</c> names a different plane's record entirely.
    /// </summary>
    private static bool NamesACleanupReceipt(string table, string column) =>
        (column.EndsWith("cleanup_receipt_id", StringComparison.Ordinal) && table != "agent_run_cleanup_receipt")
        || (table == "paired_qualification_result_pin" && column == "pinned_id");

    private static RunCleanupReceipt Receipt(RunResourceOutcome outcome) => new()
    {
        AgentRunId = Guid.NewGuid(), FenceEpoch = 1, Kind = RunResourceKind.Spool, Outcome = outcome,
        RecordedByHost = "test", RecordedAt = DateTimeOffset.UtcNow,
    };

    private static string Source() =>
        File.ReadAllText(Path.Combine(ProductionSourceRoot(), "CodeSpace.Core", "Services", "Workflows", "Retention", "Cursors", $"{nameof(CleanupReceiptRetentionCursor)}.cs"));

    /// <summary>The arguments of every <c>Any</c>/<c>AnyAsync</c> in the text — the "does anything still name this" questions, which is what makes a listed table a citation site.</summary>
    private static string ExistenceQuestions(string source)
    {
        var questions = new System.Text.StringBuilder();

        foreach (var call in new[] { "AnyAsync(", ".Any(" })
        {
            for (var index = source.IndexOf(call, StringComparison.Ordinal); index >= 0; index = source.IndexOf(call, index + 1, StringComparison.Ordinal))
            {
                var end = source.IndexOf('\n', index);

                questions.Append(source[index..(end < 0 ? source.Length : end)]);
            }
        }

        return questions.ToString();
    }

    /// <summary>One method's text, from its declaration to the next member at class indentation.</summary>
    private static string MethodBody(string source, string name)
    {
        var start = DeclarationOf(source, name);

        if (start < 0) return string.Empty;

        var next = source.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);

        return source[start..(next < 0 ? source.Length : next)];
    }

    /// <summary>The DECLARATION of a method, not the first mention of it: a probe is called from elsewhere in the file, and a search that stopped there would read the caller instead.</summary>
    private static int DeclarationOf(string source, string name)
    {
        for (var index = source.IndexOf($"{name}(", StringComparison.Ordinal); index >= 0; index = source.IndexOf($"{name}(", index + 1, StringComparison.Ordinal))
        {
            var line = source[(source.LastIndexOf('\n', index) + 1)..index];

            if (line.Contains("public", StringComparison.Ordinal) || line.Contains("private", StringComparison.Ordinal)) return index;
        }

        return -1;
    }

    private static string MemberOf(CodeSpaceDbContext db, string table, string column)
    {
        var entity = db.Model.GetEntityTypes().FirstOrDefault(type => type.GetTableName() == table)
            ?? throw new InvalidOperationException($"'{table}' is not mapped, so a citation site names a table this build does not have.");

        return (entity.GetProperties().FirstOrDefault(property => property.GetColumnName() == column)
            ?? throw new InvalidOperationException($"'{table}.{column}' is not mapped, so a citation site names a column this build does not have.")).Name;
    }

    private static string ProductionSourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "backend", "src");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException($"'backend/src' was not found above '{AppContext.BaseDirectory}', so the probes were never checked. Run the unit suite from the repository checkout.");
    }

    private static CodeSpaceDbContext BuildContext() =>
        new(new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(UnreachableDatabase).UseSnakeCaseNamingConvention().Options);
}

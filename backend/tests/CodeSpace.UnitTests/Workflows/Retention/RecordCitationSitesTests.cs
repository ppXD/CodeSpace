using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.Core.Services.Workflows.Retention.Cursors;
using CodeSpace.Messages.Agents.Recovery;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Retention;

/// <summary>
/// What may still cite a cleanup receipt and a budget reservation, and which of their states a sweep may touch at all.
/// Both lists are the correctness of their cursors, exactly as <c>ArtifactReferenceOracle.ReferenceSites</c> is for
/// artifacts: a citer missing from one makes its cursor delete a row something still points at.
///
/// <para>Each list is also cross-checked against the cursor's own source, so an entry ADDED without a probe beside it
/// reds too — a pinned list nothing checks is a comment with a test around it.</para>
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
    /// The outcomes a sweep may reclaim, and the one it may not. An Orphaned receipt is ADDRESSED to a sweep on the
    /// host that owes the work and can still become Compensated; removing it is the only way to make a resource nobody
    /// will ever be told about again. Unknown is in the list because nothing revisits it — the orphan sweep's queue is
    /// a partial index on <c>outcome = 'Orphaned'</c> — so waiting for it to settle waits for ever.
    /// </summary>
    [Fact]
    public void A_sweep_may_reclaim_every_settled_outcome_and_never_an_orphan()
    {
        CleanupReceiptRetentionCursor.SettledOutcomes.ShouldBe(
            [RunResourceOutcome.Completed, RunResourceOutcome.Compensated, RunResourceOutcome.Unknown], ignoreOrder: true);
        CleanupReceiptRetentionCursor.SettledOutcomes.ShouldNotContain(RunResourceOutcome.Orphaned,
            "an orphan names the host that still owes the teardown; reclaiming it is how a leaked resource becomes invisible");
        CleanupReceiptRetentionCursor.SettledOutcomes.Length.ShouldBe(Enum.GetValues<RunResourceOutcome>().Length - 1);
    }

    [Fact]
    public void Every_citer_of_a_terminal_budget_reservation_is_pinned()
    {
        BudgetReservationRetentionCursor.CitationSites.ShouldBe(new[]
        {
            // The physical model-call receipt that spent under this claim. Also ON DELETE RESTRICT (migration 0200),
            // so the database refuses too — but a refusal that arrives as a constraint violation is a sweep-wide
            // error, while one that arrives as a verdict is a single row kept and counted.
            ("workflow_run_model_call_attempt", "budget_reservation_id"),

            // A child claim accounted under this one (a wave under a run, an agent under a wave).
            ("budget_reservation", "parent_reservation_id"),
        }, ignoreOrder: true);
    }

    /// <summary>
    /// The states a claim can be reclaimed from — an allow-list, so a state ADDED to the ledger is out of scope until
    /// someone puts it here, rather than in scope the moment it is not named "live". Every live state is excluded, and
    /// the two sets together have to cover the whole vocabulary or a state exists that neither names.
    /// </summary>
    [Fact]
    public void A_sweep_may_reclaim_only_the_states_nothing_can_change_again()
    {
        BudgetReservationRetentionCursor.TerminalStates.ShouldBe(
            [BudgetReservationStates.Settled, BudgetReservationStates.Released, BudgetReservationStates.Expired, BudgetReservationStates.Reconciled], ignoreOrder: true);

        foreach (var live in BudgetReservationStates.Live)
            BudgetReservationRetentionCursor.TerminalStates.ShouldNotContain(live, $"'{live}' is still holding cap, and reclaiming it hands a team back money it has not finished spending");

        BudgetReservationRetentionCursor.TerminalStates.Length.ShouldBe(StateVocabulary().Count - BudgetReservationStates.Live.Count,
            "every state is either live or terminal; one that is neither is a row no sweep would ever look at and nobody would notice");
    }

    [Theory]
    [InlineData(nameof(CleanupReceiptRetentionCursor))]
    [InlineData(nameof(BudgetReservationRetentionCursor))]
    public void Every_pinned_citation_site_is_actually_probed_by_its_cursor(string cursor)
    {
        using var db = BuildContext();
        var source = File.ReadAllText(Path.Combine(ProductionSourceRoot(), "CodeSpace.Core", "Services", "Workflows", "Retention", "Cursors", $"{cursor}.cs"));
        var sites = cursor == nameof(CleanupReceiptRetentionCursor) ? CleanupReceiptRetentionCursor.CitationSites : BudgetReservationRetentionCursor.CitationSites;

        // The existence questions only: both cursors read their own table's columns all over the file, so a wider
        // match would keep passing with a probe deleted — the exact hole this check is for.
        var probes = ExistenceQuestions(source);
        probes.ShouldNotBeNullOrWhiteSpace($"{cursor} asks no existence question at all, so this check would pass by reading nothing");

        var unprobed = sites.Select(site => (site.Table, site.Column, Member: MemberOf(db, site.Table, site.Column)))
            .Where(site => !probes.Contains($".{site.Member}", StringComparison.Ordinal))
            .Select(site => $"{site.Table}.{site.Column} (no use of .{site.Member} in the citation probes)")
            .ToList();

        unprobed.ShouldBeEmpty($"a site {cursor} lists but never asks about is a claim it does not keep:\n  " + string.Join("\n  ", unprobed));
    }

    /// <summary>Every state constant the ledger declares — the denominator the live/terminal split has to cover.</summary>
    private static IReadOnlyList<string> StateVocabulary() => typeof(BudgetReservationStates)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .ToList();

    /// <summary>The arguments of every <c>Any</c>/<c>AnyAsync</c> in the source — the "does anything still name this" questions, which is what makes a listed table a citation site.</summary>
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

using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

/// <summary>
/// Makes <see cref="WorkflowRunDataNames.All"/> a CHECKED statement instead of a described one. Every registered name
/// must either resolve to a table EF actually maps on <see cref="CodeSpaceDbContext"/>, or appear in
/// <see cref="ForwardDeclarations"/> below — the honest, enumerated record of which names are still promises.
///
/// <para>Why a test and not a comment: the contract's own doc-comment used to call these "physical tables" while six of
/// them named nothing, so a reader building against the list could not tell a shipped table from a reserved word. A
/// test cannot drift — adding a name with no table fails here until it is either mapped or written down as pending, and
/// shipping one fails the second assertion until it is removed from the pending list.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class WorkflowRunDataNamesReachabilityTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused";

    /// <summary>
    /// Registered names with NO EF entity behind them, each a deliberate reservation made ahead of the slice that will
    /// build it. Two have a declared Messages record and no table: <c>HarnessDescriptor</c> and
    /// <c>RunnerHandleEnvelope</c>. Four are names only, with no type either — the log-stream, log-segment, session and
    /// session-state-revision planes. Delete an entry the moment its table lands; that deletion is the whole point.
    /// </summary>
    private static readonly string[] ForwardDeclarations =
    {
        WorkflowRunDataNames.HarnessDescriptor,
        WorkflowRunDataNames.RunnerHandle,
        WorkflowRunDataNames.LogStream,
        WorkflowRunDataNames.LogSegment,
        WorkflowRunDataNames.Session,
        WorkflowRunDataNames.SessionStateRevision,
    };

    [Fact]
    public void Every_registered_name_is_either_a_mapped_table_or_a_listed_forward_declaration()
    {
        var mapped = MappedTableNames();

        var unaccounted = WorkflowRunDataNames.All.Where(name => !mapped.Contains(name) && !ForwardDeclarations.Contains(name, StringComparer.Ordinal)).ToList();

        unaccounted.ShouldBeEmpty($"registered but unbacked, and not listed as a forward declaration: {string.Join(", ", unaccounted)} — map the table, or add the name to ForwardDeclarations so the contract stops reading as a shipped table");
    }

    [Fact]
    public void A_forward_declaration_that_shipped_is_removed_from_the_pending_list()
    {
        var mapped = MappedTableNames();

        var shipped = ForwardDeclarations.Where(mapped.Contains).ToList();

        shipped.ShouldBeEmpty($"these now have real tables and must come off ForwardDeclarations: {string.Join(", ", shipped)} — a stale pending list understates what the plane can do");
    }

    [Fact]
    public void The_pending_list_only_names_registered_tables()
    {
        var unregistered = ForwardDeclarations.Where(name => !WorkflowRunDataNames.All.Contains(name, StringComparer.Ordinal)).ToList();

        unregistered.ShouldBeEmpty($"ForwardDeclarations names something the contract does not register: {string.Join(", ", unregistered)}");
    }

    private static HashSet<string> MappedTableNames()
    {
        using var db = BuildContext();

        return db.Model.GetEntityTypes().Select(entity => entity.GetTableName()).Where(name => name is not null).ToHashSet(StringComparer.Ordinal)!;
    }

    private static CodeSpaceDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(UnreachableDatabase).UseSnakeCaseNamingConvention().Options;

        return new CodeSpaceDbContext(options);
    }
}

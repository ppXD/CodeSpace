using System.Data.Common;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts;

/// <summary>
/// The verifier's per-row guard, pinned at the tier that can still see it.
///
/// <para>Whether a row's failure is contained is a control-flow property of a method whose every branch needs a live
/// Postgres and a live destination, so the behavioural proof lives in <c>ArtifactLocationVerifierContentionTests</c>.
/// What this tier holds is the shape those tests depend on: the settle happening INSIDE the guard, the guard answering
/// for every way the database can refuse the settle — the save and the transaction around it are separate exception
/// families — and the pass never being handed a connection somebody else is using. Those are precisely the lines that
/// move under a refactor, and moving them back is silent everywhere except in an hourly job that starts writing
/// nothing at all while still reporting a full batch.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class ArtifactLocationVerifierContainmentTests
{
    private const string VerifyOne = "private async Task<Verdict> VerifyOneAsync";
    private const string Settle = "private async Task<Verdict> SettleAsync";
    private const string Revision = "private async Task<StorageProfileRevision?> RevisionAsync";

    [Fact]
    public void Settling_a_row_happens_inside_the_guard_that_contains_that_row()
    {
        var source = MethodSource(VerifyOne);
        var settle = source.IndexOf("await SettleAsync(", StringComparison.Ordinal);
        var guard = source.IndexOf("catch (", StringComparison.Ordinal);

        settle.ShouldBeGreaterThanOrEqualTo(0, "VerifyOneAsync must still be where a row's observation becomes durable");
        guard.ShouldBeGreaterThanOrEqualTo(0, "and it must still guard the row's work");
        settle.ShouldBeLessThan(guard, "the settle must sit INSIDE the try — one line outside it and a single row's failed write ends the whole hourly batch, taking every row behind it");
    }

    [Fact]
    public void The_read_that_opens_a_row_answers_for_itself()
    {
        // The row's try cannot cover the statement that runs BEFORE it, and the first thing a row's work does is read
        // the profile revision it was written under — a database call, failing for the ordinary reasons any database
        // call fails. Unguarded it is the one door out of a row that the containment above does not close, and one
        // momentarily exhausted pool on one row ends the whole hourly pass exactly as a refused write used to.
        GuardArms(Revision).ShouldNotBeEmpty("the read that opens a row's work must answer for itself, or a pool exhausted for a second on one row still ends the batch and takes every row behind it");

        // And it must answer with the row, not with a verdict it has no standing to reach: nothing about the object
        // has been observed at this point, so the honest outcome is Inconclusive and never Unrecorded.
        MethodSource(Revision).ShouldContain("return null", customMessage: "a refusal here has to leave by the same door as a revision that is simply absent — the caller reads both as Inconclusive, which is what 'nothing was observed' means");
    }

    [Fact]
    public void A_row_that_will_record_nothing_opens_nothing_to_record_it()
    {
        // The mirror of the finding above, pointing the other way. Opening a context and a transaction for a row whose
        // answer was never about the object can produce exactly one thing that would not otherwise exist: a database
        // failure filed as Unrecorded — "we could not write down what we saw" — about a row where nothing was seen.
        // Deciding first is what keeps the two verdicts meaning what they say.
        var source = MethodSource(Settle);
        var decide = source.IndexOf("IsAboutTheObject(", StringComparison.Ordinal);
        var open = source.IndexOf("CreateDb()", StringComparison.Ordinal);

        decide.ShouldBeGreaterThanOrEqualTo(0, "the settle must still ask whether this answer is about the object at all before it does anything else");
        open.ShouldBeGreaterThanOrEqualTo(0, "and must still be the place a context is opened");
        decide.ShouldBeLessThan(open, "the verdict has to be known BEFORE anything is opened: a row that records nothing must not be able to fail at a write it was never going to make");
    }

    [Fact]
    public void A_write_the_database_refused_is_an_outcome_of_the_row_not_of_the_batch()
    {
        GuardArms(Settle).ShouldContain("DbUpdateException", customMessage: "a save the database refused is an answer about this row, and must be caught as one rather than reaching the job");

        // The narrower exception the spec named is inside the arm that is actually caught: a loser that saw a stale
        // xmin and a loser that collided on the ledger's revision index are the same race, and this table produces
        // the second one — every settle appends an event at revision+1, which the winner has already taken.
        typeof(DbUpdateConcurrencyException).IsSubclassOf(typeof(DbUpdateException)).ShouldBeTrue("the concurrency loss must remain a case of the failure this catch handles");
    }

    [Fact]
    public void The_settle_answers_for_a_transaction_it_could_neither_open_nor_commit()
    {
        // The two families are DISJOINT, which is the whole trap: catching one does not catch the other, and the
        // settle raises them from different halves of itself. SaveChangesAsync produces the EF wrapper;
        // BeginTransactionAsync and CommitAsync hand the provider's own exception straight through — and COMMIT is
        // exactly where this schema's DEFERRED constraints are checked, so a refused commit is routine. Naming only
        // the wrapper leaves those to the arm that reports Inconclusive, which asserts a cause nothing established:
        // Inconclusive is the DESTINATION failing to answer, and a refused commit is this deployment failing to write
        // down an answer it already had.
        typeof(DbUpdateException).IsAssignableTo(typeof(DbException)).ShouldBeFalse("if EF ever made this a DbException the guard below could be halved — until then, dropping either name silently drops a whole failure point");

        GuardArms(Settle).ShouldContain("DbException", customMessage: "a transaction this pass could not open or commit is the same outcome as a save it could not make: the observation was not written down");
        MethodSource(Settle).ShouldContain("Verdict.Unrecorded", customMessage: "and both must land on Unrecorded — filed as Inconclusive they send an operator to inspect a destination that answered perfectly well");
    }

    [Fact]
    public void The_pass_is_never_handed_a_connection_somebody_else_is_using()
    {
        // Catching the refusal is only half of containment. Postgres aborts the whole transaction block on a
        // constraint violation and rejects every statement after it until that block ends — so a verifier holding the
        // scoped context would swallow its own poison AND the "current transaction is aborted" errors behind it, and
        // the command's own transaction would then be rolled back with every row the pass had settled inside it. The
        // rows it could write are only durable if the row it could not never touched their connection.
        var parameters = typeof(ArtifactLocationVerifier).GetConstructors().Single().GetParameters().Select(parameter => parameter.ParameterType).ToList();

        parameters.ShouldNotContain(typeof(CodeSpaceDbContext),
            "taking the ambient context back means one aborted row discards every row the pass had already settled, including the caller's own transaction");
        parameters.ShouldContain(typeof(DbContextOptions<CodeSpaceDbContext>),
            "a row's writes need a context this verifier owns and disposes with the row, which is what the options are for");
    }

    /// <summary>
    /// The lines a method declares its catch arms on — where the exception types are named, and the only place a
    /// comment about them cannot stand in for one.
    ///
    /// <para>Reading the whole method body instead would pass on the prose that explains the arm, which is exactly the
    /// mutation this is here to catch: narrowing the arm while leaving the sentence describing it untouched.</para>
    /// </summary>
    private static string GuardArms(string signature) =>
        string.Join('\n', MethodSource(signature).Split('\n').Where(line => line.TrimStart().StartsWith("catch (", StringComparison.Ordinal)));

    /// <summary>The text of one method, bounded by the doc comment of the member that follows it.</summary>
    private static string MethodSource(string signature)
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "backend", "src", "CodeSpace.Core", "Services", "Workflows", "Artifacts", "Runtime", "ArtifactLocationVerifier.cs"));
        var start = source.IndexOf(signature, StringComparison.Ordinal);

        start.ShouldBeGreaterThanOrEqualTo(0, $"'{signature}' is the method this contract is about — a rename means rewriting this test, not dropping it");

        var end = source.IndexOf("\n    /// <summary>", start, StringComparison.Ordinal);

        return end < 0 ? source[start..] : source[start..end];
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "backend", "src", "CodeSpace.Core"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root from test output.");
    }
}

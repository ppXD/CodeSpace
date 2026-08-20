using System.Reflection;
using CodeSpace.Messages.Constants;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the <see cref="RunKinds"/> token VALUES. run_kind is a Postgres GENERATED column whose CASE (migration 0067)
/// emits these exact literals; the filter compares against these constants. If a constant value drifts from the SQL
/// CASE literal, the run-kind filter silently stops matching — this test hard-pins the wire values so the rename is a
/// compile/test-visible decision, not an invisible break (the same discipline as Rule 8's env-var pinning).
/// </summary>
public class RunKindsTests
{
    [Fact]
    public void Token_values_match_the_generated_column_CASE_literals()
    {
        RunKinds.Workflow.ShouldBe("workflow");
        RunKinds.Task.ShouldBe("task");
        RunKinds.Event.ShouldBe("event");
        RunKinds.Replay.ShouldBe("replay");
        RunKinds.Schedule.ShouldBe("schedule");
        RunKinds.Child.ShouldBe("child");
        RunKinds.Api.ShouldBe("api");
        RunKinds.Other.ShouldBe("other");
    }

    /// <summary>
    /// The half of "stay in lockstep" the value pins above cannot make. Pinning each constant against a literal catches
    /// a C#-side rename, but nothing here ever read the SQL — so a token declared on the C# side that the CASE never
    /// emits stayed green while every filter comparing against it matched no row. This reads migration 0067's
    /// generated-column CASE (the DbUp files are copied next to the test binary) and requires every declared token to
    /// appear in it, enumerated by reflection so a constant added later is checked the day it appears rather than the
    /// day someone remembers this file exists.
    ///
    /// <para>One-directional by design: <see cref="RunKinds"/> is documented as an OPEN set that new origins join by
    /// extending the CASE, so a CASE literal with no constant is legitimate and is not failed here. A constant with no
    /// CASE literal is the defect — it names a run kind the database can never produce.</para>
    /// </summary>
    [Fact]
    public void Every_token_appears_in_the_generated_column_CASE()
    {
        var caseBody = GeneratedColumnCase();

        foreach (var (name, value) in DeclaredTokens())
            caseBody.ShouldContain($"'{value}'", Case.Sensitive, WhyTheCaseMustEmitIt(name, value));
    }

    /// <summary>Why a failure above matters and which side to fix — run_kind is GENERATED, so only the CASE can produce a token.</summary>
    private static string WhyTheCaseMustEmitIt(string name, string value) =>
        $"RunKinds.{name} is \"{value}\", which {CaseMigration}'s run_kind CASE never emits, so every filter comparing against it matches no row. "
        + "run_kind is a GENERATED column: the CASE is the only thing that can produce the token. Add the origin to the CASE in a NEW migration, or drop the constant.";

    /// <summary>The <c>run_kind</c> generated column's CASE, sliced out of the migration that defines it so a token quoted elsewhere in the file (the COMMENT ON COLUMN, a note) cannot pass for a CASE literal.</summary>
    private static string GeneratedColumnCase()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles", CaseMigration));
        var start = sql.IndexOf("CASE", StringComparison.Ordinal);
        var end = start < 0 ? -1 : sql.IndexOf("END", start + 1, StringComparison.Ordinal);

        start.ShouldBeGreaterThanOrEqualTo(0, $"{CaseMigration} no longer contains a CASE — run_kind's generated column moved, so this pin guards nothing and must be re-pointed at wherever the CASE now lives");
        end.ShouldBeGreaterThan(start, $"{CaseMigration}'s CASE has no END");

        return sql[start..end];
    }

    private const string CaseMigration = "0067_workflow_run_kinds.sql";

    /// <summary>Every token <see cref="RunKinds"/> declares, by reflection over its string constants, so this pin covers one added after it was written.</summary>
    private static IEnumerable<(string Name, string Value)> DeclaredTokens() =>
        typeof(RunKinds).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (f.Name, (string)f.GetRawConstantValue()!));
}

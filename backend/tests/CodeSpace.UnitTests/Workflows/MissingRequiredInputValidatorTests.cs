using CodeSpace.Core.Services.Workflows.Engine;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Unit pins for the fail-fast guard against silent-null on missing required inputs. The
/// validator throws <see cref="MissingRequiredInputException"/> whenever a declared-Required
/// input was not populated by <c>BuildInputScope</c> (neither caller-supplied nor default-filled),
/// so the run lands in Failed instead of resolving <c>{{input.x}}</c> to null. Edge cases:
/// all-satisfied is a no-op, empty required-set is a no-op, missing names are sorted for
/// deterministic operator-facing text.
/// </summary>
[Trait("Category", "Unit")]
public class MissingRequiredInputValidatorTests
{
    private static readonly Guid TeamId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid WorkflowId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static MissingRequiredInputContext Ctx(IReadOnlyCollection<string> required, IReadOnlyCollection<string> resolved) =>
        new(required, resolved, TeamId, WorkflowId);

    [Fact]
    public void Does_not_throw_when_all_required_inputs_are_resolved()
    {
        var ctx = Ctx(new[] { "name", "email" }, new[] { "name", "email" });

        Should.NotThrow(() => MissingRequiredInputValidator.EnsureSatisfied(ctx));
    }

    [Fact]
    public void Empty_required_set_is_a_no_op_even_when_resolved_set_is_empty()
    {
        // Most workflows have zero Required inputs — fast-exit path. Pin it explicitly so a
        // future refactor that inverts the guard doesn't start failing the empty-definition case.
        var ctx = Ctx(Array.Empty<string>(), Array.Empty<string>());

        Should.NotThrow(() => MissingRequiredInputValidator.EnsureSatisfied(ctx));
    }

    [Fact]
    public void Throws_naming_the_missing_inputs_and_the_workflow_and_team()
    {
        var ctx = Ctx(new[] { "ghost-a", "ghost-b", "name" }, new[] { "name" });

        var ex = Should.Throw<MissingRequiredInputException>(() => MissingRequiredInputValidator.EnsureSatisfied(ctx));

        ex.Message.ShouldContain("ghost-a");
        ex.Message.ShouldContain("ghost-b");
        ex.Message.ShouldNotContain("name]", customMessage: "a resolved input must not be reported as missing");
        ex.Message.ShouldContain(WorkflowId.ToString());
        ex.Message.ShouldContain(TeamId.ToString());
    }

    [Fact]
    public void Missing_names_are_sorted_for_determinism()
    {
        // The validator sorts missing names so operator-facing text is deterministic across
        // runs (helpful when greping logs / comparing diff'd Failed runs).
        var ctx = Ctx(new[] { "zebra", "alpha", "mango" }, Array.Empty<string>());

        var ex = Should.Throw<MissingRequiredInputException>(() => MissingRequiredInputValidator.EnsureSatisfied(ctx));

        ex.Message.IndexOf("alpha", StringComparison.Ordinal).ShouldBeLessThan(ex.Message.IndexOf("mango", StringComparison.Ordinal));
        ex.Message.IndexOf("mango", StringComparison.Ordinal).ShouldBeLessThan(ex.Message.IndexOf("zebra", StringComparison.Ordinal));
    }
}

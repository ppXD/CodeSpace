using CodeSpace.Core.Services.Workflows.Engine;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Unit pins for the fail-fast guard against silent-null on missing project refs. The validator
/// throws <see cref="MissingProjectRefException"/> whenever a workflow references a project slug
/// that the DB lookup did not return, so the run lands in Failed instead of resolving
/// <c>{{project.slug.x}}</c> to null. Edge cases: all-found is a no-op, empty referenced-set is a
/// no-op, missing slugs are sorted for deterministic operator-facing text.
/// </summary>
[Trait("Category", "Unit")]
public class MissingProjectRefValidatorTests
{
    private static readonly Guid TeamId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid WorkflowId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static MissingProjectRefContext Ctx(IReadOnlyCollection<string> referenced, IReadOnlyCollection<string> found) =>
        new(referenced, found, TeamId, WorkflowId);

    [Fact]
    public void Does_not_throw_when_all_referenced_slugs_are_found()
    {
        var ctx = Ctx(new[] { "billing", "auth" }, new[] { "billing", "auth" });

        Should.NotThrow(() => MissingProjectRefValidator.EnsureKnown(ctx));
    }

    [Fact]
    public void Empty_referenced_set_is_a_no_op_even_when_found_set_is_empty()
    {
        // Most runs reference no projects — fast-exit path. Pin it explicitly so a future
        // refactor that inverts the guard doesn't start failing the no-ref case.
        var ctx = Ctx(Array.Empty<string>(), Array.Empty<string>());

        Should.NotThrow(() => MissingProjectRefValidator.EnsureKnown(ctx));
    }

    [Fact]
    public void Throws_naming_the_missing_slugs_and_the_workflow_and_team()
    {
        var ctx = Ctx(new[] { "ghost-a", "ghost-b", "billing" }, new[] { "billing" });

        var ex = Should.Throw<MissingProjectRefException>(() => MissingProjectRefValidator.EnsureKnown(ctx));

        ex.Message.ShouldContain("ghost-a");
        ex.Message.ShouldContain("ghost-b");
        ex.Message.ShouldNotContain("billing]", customMessage: "a found slug must not be reported as missing");
        ex.Message.ShouldContain(WorkflowId.ToString());
        ex.Message.ShouldContain(TeamId.ToString());
    }

    [Fact]
    public void Missing_slugs_are_sorted_for_determinism()
    {
        var ctx = Ctx(new[] { "zebra", "alpha", "mango" }, Array.Empty<string>());

        var ex = Should.Throw<MissingProjectRefException>(() => MissingProjectRefValidator.EnsureKnown(ctx));

        ex.Message.IndexOf("alpha", StringComparison.Ordinal).ShouldBeLessThan(ex.Message.IndexOf("mango", StringComparison.Ordinal));
        ex.Message.IndexOf("mango", StringComparison.Ordinal).ShouldBeLessThan(ex.Message.IndexOf("zebra", StringComparison.Ordinal));
    }
}

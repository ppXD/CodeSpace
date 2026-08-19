using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Runtime;

/// <summary>
/// The shared attempt-generation key every CAS caller now gets, whatever plane it writes from. The runtime counts the
/// keys a scope has already burned with "the scope itself, or the scope followed by <c>/g</c>", so these are the
/// properties that query depends on being true of the key shape.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ArtifactCasIdempotencyScopeTests
{
    [Fact]
    public void Generation_zero_is_the_bare_scope_so_a_healthy_destination_never_mints_a_second_key()
    {
        ArtifactCasRuntimeCoordinator.IdempotencyKeyFor("agent-run-log/abc/1", 0).ShouldBe("agent-run-log/abc/1");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public void A_scope_that_ends_in_a_number_cannot_step_a_longer_sibling_scopes_generations(int generation)
    {
        // The reason the count matches "/g" rather than any prefix: a log stream's scope ends in its segment ordinal,
        // so ordinal 1's scope IS a prefix of ordinal 10's. If the burned-key count were prefix-only, one failed
        // ordinal-1 write would silently step the generation of every ordinal it happens to prefix.
        var first = ArtifactCasRuntimeCoordinator.IdempotencyKeyFor("agent-run-log/abc/1", generation);
        var tenth = ArtifactCasRuntimeCoordinator.IdempotencyKeyFor("agent-run-log/abc/10", generation);

        first.ShouldStartWith("agent-run-log/abc/1/g");
        tenth.ShouldNotStartWith("agent-run-log/abc/1/g");
        tenth.ShouldNotBe("agent-run-log/abc/1");
    }

    [Fact]
    public void A_scope_at_its_accepted_cap_can_still_mint_every_generation_the_column_can_hold()
    {
        // The scope cap exists only to reserve room for the suffix. If that arithmetic is ever wrong, a burned scope
        // stops being escapable at exactly the moment the repair happens — the column refuses the next key.
        var scope = new string('s', ArtifactCasTransferRequest.MaximumScopeLength);

        ArtifactCasRuntimeCoordinator.IdempotencyKeyFor(scope, int.MaxValue).Length.ShouldBeLessThanOrEqualTo(ArtifactCasTransferRequest.MaximumKeyLength);
    }
}

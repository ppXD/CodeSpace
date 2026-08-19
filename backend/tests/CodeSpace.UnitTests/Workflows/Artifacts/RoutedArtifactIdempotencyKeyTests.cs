using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts;

/// <summary>
/// The intent key a routed write claims. Its shape is load-bearing in two opposite directions at once: it must be
/// IDENTICAL for concurrent writers of the same bytes, so they share one transfer instead of racing two, and it must
/// be DIFFERENT once a non-retryable failure has burned a generation, because the database guard offers no route back
/// out of Failed and only a distinct key can mint a fresh intent under
/// <c>ux_artifact_transfer_intent_idempotency (team_id, storage_profile_revision_id, idempotency_key)</c>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RoutedArtifactIdempotencyKeyTests
{
    private const string Sha = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    [Fact]
    public void The_first_generation_is_the_bare_content_key_so_concurrent_writers_share_one_transfer()
    {
        // A healthy destination never mints a second key, so the shared-intent behaviour is the DEFAULT and the
        // discriminator costs nothing until something actually fails. This exact string is also what every intent
        // committed before generations existed carries, so it has to keep resolving to the same row.
        ArtifactCasRuntimeCoordinator.IdempotencyKeyFor(ArtifactStore.IdempotencyScopeFor(Sha), 0).ShouldBe($"{WorkflowArtifactDestinationResolver.DataClassTypeKey}/{Sha}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(97)]
    public void A_later_generation_is_a_distinct_key_that_still_carries_the_content_prefix(int generation)
    {
        // Distinct, or a repaired configuration replays the burned intent and the content stays unstorable forever.
        // Prefixed — with the /g the runtime's counting query matches on — or those burned generations stop being
        // counted and every later attempt is handed the same already-Failed key.
        var content = ArtifactStore.IdempotencyScopeFor(Sha);
        var later = ArtifactCasRuntimeCoordinator.IdempotencyKeyFor(content, generation);

        later.ShouldNotBe(content);
        later.ShouldStartWith($"{content}/g");
        later.Length.ShouldBeLessThanOrEqualTo(ArtifactCasTransferRequest.MaximumKeyLength, "idempotency_key is VARCHAR(256)");
    }

    [Fact]
    public void Generations_never_collide_with_each_other_or_across_payloads()
    {
        // One payload's scope must never be another payload's scope, nor a /g-generation of it, or the runtime's
        // burned-key count would step generations for content that never failed. The sha is fixed-width hex, which is
        // what makes that true for the routed plane.
        var other = new string('a', 64);

        Enumerable.Range(0, 8).Select(generation => ArtifactCasRuntimeCoordinator.IdempotencyKeyFor(ArtifactStore.IdempotencyScopeFor(Sha), generation)).Distinct().Count().ShouldBe(8);
        ArtifactStore.IdempotencyScopeFor(other).ShouldNotStartWith(ArtifactStore.IdempotencyScopeFor(Sha));
        ArtifactStore.IdempotencyScopeFor(Sha).ShouldNotStartWith(ArtifactStore.IdempotencyScopeFor(other));
    }
}

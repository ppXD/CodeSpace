using Autofac;
using CodeSpace.Core.Failures;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Queries.Artifacts;
using MediatR;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts;

/// <summary>
/// What a reader is told when a stored object is no longer there.
///
/// <para>The storage plane distinguishes five reasons and the read surface published none of them, so a client had
/// nothing to render but a guess. "It may have been removed from its storage destination" is wrong for four of the
/// five: a revoked key sends the operator hunting deleted data that is sitting untouched at the destination. The
/// reason has to travel on the failure the way <c>IFailure.Details</c> says it does — the fields a caller needs in
/// order to ACT.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RottedArtifactReadReasonTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly List<string> _roots = [];

    public RottedArtifactReadReasonTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Reading_an_object_the_destination_no_longer_holds_carries_the_reason_a_client_renders()
    {
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var destination = await RoutedArtifactSeed.RouteTeamAsync(_fixture, teamId, actorId);
        _roots.Add(destination.Root);
        var artifactId = await RoutedArtifactSeed.WriteRoutedAsync(_fixture, teamId, "about to rot", "text/plain");

        EmptyDestination(destination.Root);

        var failure = await ReadAsync(teamId, actorId, artifactId);

        failure.Kind.ShouldBe(ArtifactContentUnavailableKind.PhysicalObjectMissing);
        FailureClassifier.Classify(failure).Details.ShouldNotBeNull("the body a client reads is built from Details; a reason that stops here reaches nobody")
            ["reason"].ShouldBe(nameof(ArtifactContentUnavailableKind.PhysicalObjectMissing));
    }

    [Fact]
    public async Task The_published_reason_is_the_kind_itself_so_a_client_can_branch_on_every_lane()
    {
        // Pinned as a wire contract rather than sampled from one lane: a client switches exhaustively on these five
        // names, and an abbreviated or re-cased one silently collapses into its fallback.
        foreach (var kind in Enum.GetValues<ArtifactContentUnavailableKind>())
        {
            var details = FailureClassifier.Classify(new ArtifactContentUnavailableException(Guid.NewGuid(), kind)).Details;

            details.ShouldNotBeNull();
            details["reason"].ShouldBe(kind.ToString());
        }
    }

    private async Task<ArtifactContentUnavailableException> ReadAsync(Guid teamId, Guid actorId, Guid artifactId)
    {
        using var scope = _fixture.BeginScopeAs(actorId, teamId);

        return await Should.ThrowAsync<ArtifactContentUnavailableException>(
            () => scope.Resolve<IMediator>().Send(new GetArtifactQuery { ArtifactId = artifactId }));
    }

    /// <summary>Takes the objects and leaves the destination, which is what an emptied bucket looks like to a reader.</summary>
    private static void EmptyDestination(string root)
    {
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.Delete(file);
    }

    public void Dispose()
    {
        foreach (var root in _roots.Where(Directory.Exists))
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}

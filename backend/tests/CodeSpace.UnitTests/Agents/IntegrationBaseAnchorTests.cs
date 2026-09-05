using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Workspace.Integrators;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The ONE anchor rule both integrate lanes resolve <c>IntegrationRequest.BaseSha</c> from: the ANCESTOR-MOST base
/// of the run, realized as the OLDEST base its publish ledger recorded for the repository.
///
/// <para>Pins what makes the rule load-bearing: it reads the LEDGER, so a producer that was withheld from the head
/// or excluded as unintegrable still names the run's root even though it is gone from the contribution list — the
/// exact case where the surviving contributions are all dependents cut from that producer's head, and anchoring on
/// the first of them refuses every sibling still rooted at the repository base.</para>
/// </summary>
public class IntegrationBaseAnchorTests
{
    private static readonly Guid Repo = Guid.NewGuid();
    private static readonly Guid OtherRepo = Guid.NewGuid();

    [Fact]
    public void Anchors_on_the_oldest_recorded_base_not_the_newest()
    {
        var manifests = new[] { Manifest(Repo, "dependent-base", minutesAgo: 1), Manifest(Repo, "run-root", minutesAgo: 9) };

        IntegrationBaseAnchor.OldestRecordedBase(manifests, Repo).ShouldBe("run-root",
            customMessage: "a run only re-parents FORWARD, so the first base it recorded is the ancestor of every base recorded after it");
    }

    [Fact]
    public void Anchors_on_a_producer_that_is_gone_from_the_contributions()
    {
        // The producer's grade rejected it (or it captured no work), so it contributes nothing to the merge — but its
        // manifest row survives, and it is the only thing that still names the commit the dependent's chain hangs off.
        var manifests = new[]
        {
            Manifest(Repo, "producer-head", minutesAgo: 2),
            Manifest(Repo, "run-root", minutesAgo: 8, acceptance: PublishAcceptanceState.Failed),
        };

        IntegrationBaseAnchor.OldestRecordedBase(manifests, Repo).ShouldBe("run-root",
            customMessage: "a WITHHELD producer is dropped from the contributions, never from the ledger — that is why the anchor is read here");
    }

    [Fact]
    public void Reads_only_this_repositorys_agent_rows_with_a_base()
    {
        var manifests = new[]
        {
            Manifest(OtherRepo, "another-repos-root", minutesAgo: 30),
            Manifest(Repo, null, minutesAgo: 20),
            Manifest(Repo, "an-earlier-integration", minutesAgo: 15, kind: PublishManifestKind.Integration),
            Manifest(Repo, "run-root", minutesAgo: 10),
        };

        IntegrationBaseAnchor.OldestRecordedBase(manifests, Repo).ShouldBe("run-root",
            customMessage: "a sibling repo's root, a base-less row and a PRIOR integration's own base are all older, and none of them is this repository's agent root");
    }

    [Fact]
    public void Is_order_independent_and_null_when_nothing_recorded_a_base()
    {
        var manifests = new[] { Manifest(Repo, "run-root", minutesAgo: 9), Manifest(Repo, "dependent-base", minutesAgo: 1) };

        IntegrationBaseAnchor.OldestRecordedBase(manifests, Repo).ShouldBe("run-root",
            customMessage: "the reduction sorts for itself — it must not depend on the order the store happened to return");
        IntegrationBaseAnchor.OldestRecordedBase(Array.Empty<PublishManifest>(), Repo).ShouldBeNull(
            customMessage: "no recorded base leaves the caller its own first-eligible-contribution fallback");
    }

    private static PublishManifest Manifest(Guid repositoryId, string? baseSha, int minutesAgo, PublishManifestKind kind = PublishManifestKind.Agent, PublishAcceptanceState acceptance = PublishAcceptanceState.NotApplicable) => new()
    {
        Id = Guid.NewGuid(),
        Kind = kind,
        RepositoryId = repositoryId,
        BaseSha = baseSha,
        AcceptanceState = acceptance,
        CreatedDate = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
    };
}

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

    /// <summary>
    /// <see cref="PublishManifest.RepositoryId"/> post-dates the manifest table, and every manifest-backed consumer
    /// honours the null row as the legacy single-repository carrier (<c>PublishManifestRepositorySelector</c>).
    /// Filtering on the concrete id alone reads a legacy run's ledger as EMPTY and drops it to the caller's
    /// first-contribution fallback — the very anchor this rule replaces, so the defect survives untouched exactly on
    /// the runs that predate the column.
    /// </summary>
    [Fact]
    public void A_legacy_ledger_that_never_recorded_a_repository_still_names_the_runs_root()
    {
        var manifests = new[] { Manifest(null, "dependent-base", minutesAgo: 1), Manifest(null, "run-root", minutesAgo: 9) };

        IntegrationBaseAnchor.OldestRecordedBase(manifests, Repo).ShouldBe("run-root",
            customMessage: "a pre-column run recorded ONE repository under a null id — dropping those rows leaves the legacy run on the anchor the ledger exists to replace");
    }

    /// <summary>The bound the legacy tier is drawn at, and the reason it cannot leak: the same "a concrete mismatch never inherits the compatibility fallback" rule <c>PublishManifestRepositorySelector</c> draws. Once ANY row carries a concrete repository the ledger is post-column, so a null row in it is not this repository's evidence.</summary>
    [Fact]
    public void A_legacy_row_beside_a_concrete_one_is_never_read_as_this_repositorys_root()
    {
        var manifests = new[] { Manifest(null, "unattributed-base", minutesAgo: 20), Manifest(OtherRepo, "another-repos-root", minutesAgo: 10) };

        IntegrationBaseAnchor.OldestRecordedBase(manifests, Repo).ShouldBeNull(
            customMessage: "handing one repository's root to another repository's integration is the graft the anchor exists to prevent — the caller's own fallback stands instead");
    }

    /// <summary>
    /// The tier's OTHER escape, and the one a live run can still reach: <see cref="PublishManifest.RepositoryId"/> is
    /// "the catalog repository, WHEN RESOLVED", so a current multi-repository run over unresolved repositories writes
    /// an all-null ledger — and the merge / <c>git.integrate_run</c> lanes hand this rule the run-wide, ALL-repository
    /// ledger. "No concrete row anywhere" therefore does not mean "one repository": repository X's integration would
    /// be anchored on repository Y's root, which the integrator refuses as a base that is not in the clone at all.
    /// </summary>
    [Fact]
    public void A_multi_repository_ledger_of_unresolved_rows_never_inherits_the_compatibility_tier()
    {
        var manifests = new[] { Manifest(null, "another-repos-root", minutesAgo: 20, alias: "docs"), Manifest(null, "this-repos-root", minutesAgo: 10, alias: "api") };

        IntegrationBaseAnchor.OldestRecordedBase(manifests, Repo).ShouldBeNull(
            customMessage: "two workspace aliases are two repositories, resolved id or not — the oldest of them is another repository's root, so the caller's own fallback must stand");
    }

    /// <summary>A ledger that mixes an unresolved row with a concrete one across two workspace aliases is post-column AND multi-repository: only the repository-scoped rows are this repository's evidence, exactly as when the mismatch is concrete on both sides.</summary>
    [Fact]
    public void A_mixed_ledger_spanning_two_repositories_reads_only_the_repository_scoped_rows()
    {
        var manifests = new[] { Manifest(null, "another-repos-root", minutesAgo: 20, alias: "docs"), Manifest(Repo, "run-root", minutesAgo: 10, alias: "api") };

        IntegrationBaseAnchor.OldestRecordedBase(manifests, Repo).ShouldBe("run-root",
            customMessage: "the older row belongs to a different workspace alias, so it is not an ancestor of anything in THIS repository");
    }

    /// <summary>
    /// The staging lane's shape: <c>ResolveProducerManifestsAsync</c> selects each producer's row through
    /// <c>PublishManifestRepositorySelector</c>, which returns a sole null-id row as the legacy carrier — so one
    /// producer set can carry a null row beside a concrete one for the SAME repository. Reading only the concrete
    /// rows anchors the handoff on the re-parented producer's downstream base and refuses its own sibling.
    /// </summary>
    [Fact]
    public void One_repositorys_unresolved_row_still_names_the_root_beside_its_concrete_sibling()
    {
        var manifests = new[] { Manifest(null, "run-root", minutesAgo: 20), Manifest(Repo, "producer-head", minutesAgo: 10) };

        IntegrationBaseAnchor.OldestRecordedBase(manifests, Repo).ShouldBe("run-root",
            customMessage: "one alias is one repository, so the unresolved row is this repository's own earlier evidence — anchoring on the concrete downstream base refuses the row that recorded the root");
    }

    /// <summary>The shape all three integrate lanes call: the ledger's root when it has one, else the caller's own first-contribution base — the pre-ledger behaviour, preserved verbatim.</summary>
    [Fact]
    public void The_callers_first_contribution_base_stands_only_when_the_ledger_recorded_none()
    {
        IntegrationBaseAnchor.Resolve(new[] { Manifest(Repo, "run-root", minutesAgo: 9) }, Repo, "first-contribution").ShouldBe("run-root");
        IntegrationBaseAnchor.Resolve(Array.Empty<PublishManifest>(), Repo, "first-contribution").ShouldBe("first-contribution",
            customMessage: "an empty ledger must leave the lane exactly where it was before the anchor existed");
        IntegrationBaseAnchor.Resolve(Array.Empty<PublishManifest>(), Repo, null).ShouldBeNull(
            customMessage: "neither a ledger nor a contribution names a base — the caller must be able to see that and refuse, never integrate from an invented commit");
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

    /// <summary>
    /// The three lanes that anchor an integration must all read THIS rule. A lane that quietly re-derives its own
    /// base is invisible to every behavioural test above — it simply keeps the defect, so the pin is on the call
    /// sites themselves.
    /// </summary>
    [Theory]
    [InlineData("backend/src/CodeSpace.Core/Services/Supervisor/Executors/RealSupervisorActionExecutor.Integrate.cs")]
    [InlineData("backend/src/CodeSpace.Core/Services/Supervisor/Executors/RealSupervisorActionExecutor.DependencyStaging.cs")]
    [InlineData("backend/src/CodeSpace.Core/Services/Workflows/Nodes/Builtin/GitIntegrateRunNode.cs")]
    public void Every_integrate_lane_resolves_its_base_through_the_one_anchor(string lane)
    {
        if (FindRepositoryRoot() is not { } root) return;   // the source tree the pin reads is absent (packaged / published test run) — skip rather than fail on a coupling this assertion alone has

        File.ReadAllText(Path.Combine(root, lane)).ShouldContain($"{nameof(IntegrationBaseAnchor)}.{nameof(IntegrationBaseAnchor.Resolve)}(",
            customMessage: $"{lane} anchors an integration, so it must resolve its BaseSha through the shared rule — a private re-derivation drifts silently");
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "backend/src"))) directory = directory.Parent;
        return directory?.FullName;
    }

    private static PublishManifest Manifest(Guid? repositoryId, string? baseSha, int minutesAgo, PublishManifestKind kind = PublishManifestKind.Agent, PublishAcceptanceState acceptance = PublishAcceptanceState.NotApplicable, string alias = "primary") => new()
    {
        Id = Guid.NewGuid(),
        Kind = kind,
        RepositoryId = repositoryId,
        RepositoryAlias = alias,
        BaseSha = baseSha,
        AcceptanceState = acceptance,
        CreatedDate = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
    };
}

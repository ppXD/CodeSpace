using System.Text.Json;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: pins B2 — the per-agent repo PRIVILEGE GATE (<see cref="SupervisorRepoClamp.IntersectWithBoundRepos"/>).
/// The heaviest-tested boundary of the L4 arc: a model-authored repo subset is GRANTED only what the operator already
/// bound — every repo must be in the bound set (primary + related) and access may only DOWNGRADE; an out-of-set repo
/// or a read→write upgrade fails CLOSED with <see cref="SupervisorRepoAccessException"/>. Reuses the shared
/// related-repos parser, so the only NEW logic here is the subset + no-upgrade validation.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorRepoClampTests
{
    private static readonly Guid Primary = Guid.NewGuid();
    private static readonly Guid ApiWritable = Guid.NewGuid();   // operator bound it WRITE
    private static readonly Guid SdkReadOnly = Guid.NewGuid();   // operator bound it READ
    private static readonly Guid Unbound = Guid.NewGuid();       // never bound

    private static readonly WorkspaceRepositorySpec[] Bound =
    {
        new() { Alias = "api", RepositoryId = ApiWritable, Access = WorkspaceAccess.Write },
        new() { Alias = "sdk", RepositoryId = SdkReadOnly, Access = WorkspaceAccess.Read },
    };

    // ── Allowed: a subset within the bound set, at or below the granted access ─────────

    [Fact]
    public void A_subset_of_bound_repos_passes_through_with_its_authored_access()
    {
        var result = Clamp(Authored((ApiWritable, "write"), (SdkReadOnly, "read")));

        result.Select(r => r.RepositoryId).ShouldBe(new[] { ApiWritable, SdkReadOnly });
        result.Single(r => r.RepositoryId == ApiWritable).Access.ShouldBe(WorkspaceAccess.Write);
        result.Single(r => r.RepositoryId == SdkReadOnly).Access.ShouldBe(WorkspaceAccess.Read);
    }

    [Fact]
    public void Targeting_the_operator_primary_with_write_is_allowed()
    {
        Clamp(Authored((Primary, "write"))).Single().RepositoryId.ShouldBe(Primary, "the operator's primary is writable, so an agent may target it");
    }

    [Fact]
    public void Downgrading_a_writable_repo_to_read_is_allowed()
    {
        Clamp(Authored((ApiWritable, "read"))).Single().Access.ShouldBe(WorkspaceAccess.Read, "asking for LESS than the grant (write→read) is always safe");
    }

    [Fact]
    public void An_empty_or_non_array_subset_returns_empty()
    {
        Clamp(JsonSerializer.SerializeToElement(Array.Empty<object>())).ShouldBeEmpty("no authored subset → the agent runs single-repo on the primary");
        Clamp(JsonSerializer.SerializeToElement("not-an-array")).ShouldBeEmpty("a malformed subset parses to empty (lenient), never throws");
    }

    [Fact]
    public void A_repo_bound_as_both_primary_and_read_related_takes_the_higher_grant()
    {
        // The operator bound ApiWritable as the PRIMARY (write) AND as a read-only related — the higher grant wins, so
        // an authored write is allowed.
        var result = SupervisorRepoClamp.IntersectWithBoundRepos(
            Authored((ApiWritable, "write")), ApiWritable, new[] { new WorkspaceRepositorySpec { Alias = "api", RepositoryId = ApiWritable, Access = WorkspaceAccess.Read } });

        result.Single().Access.ShouldBe(WorkspaceAccess.Write);
    }

    [Fact]
    public void A_related_repo_is_grantable_even_with_no_bound_primary()
    {
        // Analysis-only / no-primary profile: a related repo still clamps against the bound related set.
        SupervisorRepoClamp.IntersectWithBoundRepos(Authored((ApiWritable, "write")), boundPrimaryId: null, Bound)
            .Single().RepositoryId.ShouldBe(ApiWritable);
    }

    // ── Fail-closed: out-of-set repo, or access escalation ─────────────────────────────

    [Fact]
    public void An_unbound_repository_fails_closed()
    {
        var ex = Should.Throw<SupervisorRepoAccessException>(() => Clamp(Authored((Unbound, "read"))));
        ex.Message.ShouldContain(Unbound.ToString(), Case.Insensitive);
        ex.Message.ShouldContain("did not bind", Case.Insensitive);
    }

    [Fact]
    public void A_read_to_write_upgrade_fails_closed()
    {
        var ex = Should.Throw<SupervisorRepoAccessException>(() => Clamp(Authored((SdkReadOnly, "write"))));
        ex.Message.ShouldContain("read-only", Case.Insensitive, "the model cannot escalate its write access past the operator's read-only grant");
    }

    [Fact]
    public void One_bad_repo_in_a_set_rejects_the_whole_dispatch()
    {
        // A valid repo alongside an out-of-set one fails the whole clamp — the gate never silently drops the bad repo.
        Should.Throw<SupervisorRepoAccessException>(() => Clamp(Authored((ApiWritable, "write"), (Unbound, "read"))));
    }

    // ── The PRIMARY axis: a per-agent primary override must be a WRITABLE bound repo ───

    [Fact]
    public void A_null_primary_override_keeps_the_operator_primary()
    {
        SupervisorRepoClamp.ClampPrimary(null, Primary, Bound).ShouldBe(Primary, "no override → the run's primary");
    }

    [Fact]
    public void The_operator_primary_is_a_valid_primary_override()
    {
        SupervisorRepoClamp.ClampPrimary(Primary, Primary, Bound).ShouldBe(Primary);
    }

    [Fact]
    public void A_write_granted_related_repo_may_become_the_primary()
    {
        SupervisorRepoClamp.ClampPrimary(ApiWritable, Primary, Bound).ShouldBe(ApiWritable, "the operator bound api writable, so an agent may make it its writable primary");
    }

    [Fact]
    public void A_read_only_related_repo_cannot_become_the_writable_primary()
    {
        // The escalation the subset clamp alone would miss: a read-only repo promoted to the writable primary.
        var ex = Should.Throw<SupervisorRepoAccessException>(() => SupervisorRepoClamp.ClampPrimary(SdkReadOnly, Primary, Bound));
        ex.Message.ShouldContain("read-only", Case.Insensitive);
        ex.Message.ShouldContain("primary", Case.Insensitive);
    }

    [Fact]
    public void An_unbound_repo_cannot_become_the_primary()
    {
        var ex = Should.Throw<SupervisorRepoAccessException>(() => SupervisorRepoClamp.ClampPrimary(Unbound, Primary, Bound));
        ex.Message.ShouldContain(Unbound.ToString(), Case.Insensitive);
        ex.Message.ShouldContain("did not bind", Case.Insensitive);
    }

    // ── S1: the launch base pin is SERVER truth on the clamped subset ───

    [Fact]
    public void A_clamped_subset_takes_each_bound_repos_own_pin_and_discards_a_model_authored_one()
    {
        var boundWithPins = new[]
        {
            new WorkspaceRepositorySpec { RepositoryId = ApiWritable, Alias = "api", Access = WorkspaceAccess.Write, PinnedSha = "bbb222bbb222" },
            new WorkspaceRepositorySpec { RepositoryId = SdkReadOnly, Alias = "sdk", Access = WorkspaceAccess.Read },
        };

        // The model authored its OWN pinnedSha on the api entry — a dispatch must not point a bound mount at an
        // arbitrary commit; the BOUND spec's launch pin wins (scan M2/m2), and an unpinned bound repo stays unpinned.
        var authored = JsonSerializer.SerializeToElement(new object[]
        {
            new { repositoryId = ApiWritable, access = "write", pinnedSha = "deadbeefdead" },
            new { repositoryId = SdkReadOnly, access = "read" },
        });

        var result = SupervisorRepoClamp.IntersectWithBoundRepos(authored, Primary, boundWithPins);

        result.Single(r => r.RepositoryId == ApiWritable).PinnedSha.ShouldBe("bbb222bbb222", "a dispatched agent's mounts materialize the SAME base as its homogeneous siblings");
        result.Single(r => r.RepositoryId == SdkReadOnly).PinnedSha.ShouldBeNull("no launch pin on the bound spec ⇒ none on the clamp output — never the model's invention");
    }

    // ── Helpers ───

    private static IReadOnlyList<WorkspaceRepositorySpec> Clamp(JsonElement authored) =>
        SupervisorRepoClamp.IntersectWithBoundRepos(authored, Primary, Bound);

    private static JsonElement Authored(params (Guid id, string access)[] repos) =>
        JsonSerializer.SerializeToElement(repos.Select(r => new { repositoryId = r.id, access = r.access }).ToArray());
}

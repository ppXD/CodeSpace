using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit (pure): C3 — WHICH commit the terminal stop's acceptance gates restore each repository's oracle from.
///
/// <para>The choice is the whole protection. Dependency staging hands a downstream unit a PRODUCER's branch as its
/// base, so a round-2 manifest's recorded base can already contain a round-1 candidate's edits — including its
/// edits to the very check script the operator's floor runs. Anchoring on the newest recorded base would therefore
/// restore the judge from a tree a candidate had already written to: the least protective anchor on the tape,
/// while looking like protection. The launch pin is server truth minted before any candidate ran; failing that,
/// the oldest recorded base is the closest thing to the pre-run tree.</para>
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorStopOracleAnchorTests
{
    private static readonly Guid Primary = Guid.NewGuid();
    private static readonly Guid Related = Guid.NewGuid();

    [Fact]
    public void The_launch_pin_outranks_every_recorded_base()
    {
        var bases = SupervisorTurnService.ResolveOracleBaseShas(
            Manifests((Primary, "round2base"), (Primary, "round1base")),
            Context(pinnedSha: "launchpin"));

        bases[Primary].ShouldBe("launchpin", "the S1 launch pin is SERVER truth, minted before any candidate ran — no recorded base outranks it");
    }

    [Fact]
    public void The_oldest_recorded_base_wins_when_the_launch_never_pinned()
    {
        // The store returns newest-first, so this list is round 2 then round 1.
        var bases = SupervisorTurnService.ResolveOracleBaseShas(
            Manifests((Primary, "round2base"), (Primary, "round1base")),
            Context(pinnedSha: null));

        bases[Primary].ShouldBe("round1base",
            "round 2's base can be a round-1 producer's BRANCH (dependency staging), so it may already carry that candidate's edit to the check script — "
          + "restoring the judge from it would be protection in name only");
    }

    [Fact]
    public void A_related_repos_own_pin_anchors_its_own_oracle()
    {
        var bases = SupervisorTurnService.ResolveOracleBaseShas(
            Manifests((Primary, "primarybase"), (Related, "relatedbase")),
            Context(pinnedSha: "primarypin", relatedPinnedSha: "relatedpin"));

        bases[Primary].ShouldBe("primarypin");
        bases[Related].ShouldBe("relatedpin", "one repo's anchor can never stand in for another's — a multi-repo stop grades each head against its own base");
    }

    [Fact]
    public void A_related_repo_with_no_pin_falls_back_to_its_own_oldest_base()
    {
        var bases = SupervisorTurnService.ResolveOracleBaseShas(
            Manifests((Related, "newerbase"), (Related, "olderbase")),
            Context(pinnedSha: "primarypin", relatedPinnedSha: null));

        bases[Related].ShouldBe("olderbase");
        bases.ContainsKey(Primary).ShouldBeTrue("the primary still carries its own launch pin");
    }

    [Fact]
    public void A_repo_with_neither_a_pin_nor_a_recorded_base_is_absent()
    {
        var bases = SupervisorTurnService.ResolveOracleBaseShas(
            Manifests((Primary, null), (Primary, "   ")),
            Context(pinnedSha: null));

        bases.ContainsKey(Primary).ShouldBeFalse("no anchor ⇒ no entry ⇒ the grade runs unprotected AND says so, rather than restoring from a blank sha");
    }

    [Theory]
    [InlineData(null, "accepted")]
    [InlineData("", "accepted")]
    [InlineData("ORACLE TAMPER VOIDED — check.sh", "accepted [ORACLE TAMPER VOIDED — check.sh]")]
    public void The_oracles_integrity_note_rides_the_verdict_detail(string? note, string expected)
    {
        // The durable stop outcome carries pass + detail and nothing else (SupervisorOutcome.AppendAcceptanceGrade),
        // so the detail is the ONLY route a voided tamper has to the journal, the decider prompt and the receipt.
        // No note ⇒ the detail verbatim, so an ordinary run's bytes are unchanged.
        SupervisorTurnService.Annotated("accepted", note).ShouldBe(expected);
    }

    private static IReadOnlyList<PublishManifest> Manifests(params (Guid RepositoryId, string? BaseSha)[] rows) =>
        rows.Select(r => new PublishManifest { Id = Guid.NewGuid(), RepositoryId = r.RepositoryId, BaseSha = r.BaseSha, RepositoryAlias = "primary" }).ToList();

    private static SupervisorTurnContext Context(string? pinnedSha, string? relatedPinnedSha = null) => new()
    {
        SupervisorRunId = Guid.NewGuid(),
        AgentProfile = new SupervisorAgentProfile
        {
            RepositoryId = Primary,
            PinnedSha = pinnedSha,
            RelatedRepositories = JsonSerializer.SerializeToElement(new[] { new { repositoryId = Related.ToString(), alias = "web", pinnedSha = relatedPinnedSha } }),
        },
    };
}

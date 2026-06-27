using Autofac;
using CodeSpace.Core.Services.Agents;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.E2ETests.Agents;

/// <summary>
/// The REAL-GitHub closed loop for URL pack import — the production <see cref="IPackSourceFetcher"/> (real host
/// allowlist + real git clone) clones two real public libraries and the production <see cref="IPackSourceWalker"/>
/// discovers their contents: an AGENTS library (<c>contains-studio/agents</c>) and a SKILLS library
/// (<c>obra/superpowers</c>, pinned to the immutable tag <c>v6.0.3</c> for stable assertions). This is the
/// highest-fidelity proof the pipeline works end-to-end against the real ecosystem the operator will paste URLs
/// from — not a local fixture.
///
/// <para>Skip ≠ pass, and a transient blip never reds main: a clone that cannot reach GitHub / the repo / git
/// (a <see cref="PackImportException"/>) is reported as a LOUD skip and returns; only a SUCCESSFUL clone whose
/// discovery comes back empty fails the test (the genuine regression). POSIX-only (the clone runs through the
/// local sandbox runner's git). Runs on any lane with outbound network (GitHub-hosted runners have it); skips on
/// an offline / air-gapped / fork runner.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class RealGitHubPackImportE2ETests
{
    private const string AgentsLibraryUrl = "https://github.com/contains-studio/agents";
    private const string SkillsLibraryUrl = "https://github.com/obra/superpowers";
    private const string SkillsLibraryTag = "v6.0.3";   // an immutable tag → stable discovery assertions

    private readonly PostgresFixture _fixture;

    public RealGitHubPackImportE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_agents_library_clones_from_real_github_and_its_agents_are_discovered()
    {
        if (OperatingSystem.IsWindows()) return;

        await RunDiscoveryAsync(AgentsLibraryUrl, reference: null, pack =>
        {
            pack.Agents.Count.ShouldBeGreaterThanOrEqualTo(10,
                customMessage: $"contains-studio/agents is a library of ~37 agents; discovered {pack.Agents.Count}");
            pack.Agents.Select(a => a.Name).ShouldContain("backend-architect",
                customMessage: "the well-known backend-architect agent must be discovered from the real repo");
        });
    }

    [Fact]
    public async Task A_skills_library_clones_from_real_github_at_a_pinned_tag_and_its_skills_are_discovered()
    {
        if (OperatingSystem.IsWindows()) return;

        await RunDiscoveryAsync(SkillsLibraryUrl, SkillsLibraryTag, pack =>
        {
            pack.Skills.Count.ShouldBeGreaterThanOrEqualTo(8,
                customMessage: $"obra/superpowers {SkillsLibraryTag} ships ~14 skills; discovered {pack.Skills.Count}");
            pack.Skills.Select(s => s.Name).ShouldContain("test-driven-development",
                customMessage: "the well-known test-driven-development skill must be discovered from the real repo");
        });
    }

    /// <summary>Clone the real repo via the production fetcher, walk it, and run <paramref name="assert"/> — but if the clone can't reach GitHub/the repo/git, LOUD-skip (skip ≠ pass) instead of failing, so a transient network blip never reds main. The clone is always disposed (its dir reclaimed).</summary>
    private async Task RunDiscoveryAsync(string url, string? reference, Action<DiscoveredPack> assert)
    {
        using var scope = _fixture.BeginScope();

        // An allowlist regression must RED this test, not masquerade as a clone-skip: assert the host passes the REAL
        // production allowlist up front (outside the catch below), so only a genuine network/git/repo failure skips.
        scope.Resolve<IPackHostAllowlist>().IsAllowed(url).ShouldBeTrue($"{url} must pass the production pack-source host allowlist");

        var fetcher = scope.Resolve<IPackSourceFetcher>();
        var walker = scope.Resolve<IPackSourceWalker>();

        PackCheckout checkout;
        try
        {
            checkout = await fetcher.FetchAsync(url, reference, CancellationToken.None);
        }
        catch (PackImportException ex)
        {
            // Could not reach GitHub / the repo / git — a non-gating LOUD skip (NOT a pass): nothing was cloned, so
            // nothing was verified. A real discovery regression is only observable when the clone SUCCEEDS.
            ReportSkip($"could not clone {url}{(reference is null ? "" : "@" + reference)}: {ex.Message}");
            return;
        }

        using (checkout)
        {
            var pack = await walker.WalkAsync(checkout.Directory, CancellationToken.None);
            assert(pack);
        }
    }

    /// <summary>Surface a NOT-EVALUATED skip where CI actually shows it — the GitHub step-summary FILE (xUnit CAPTURES Console, so a raw Console.WriteLine would be invisible in the job UI), else the console for a local run. Mirrors RealModelGate.ReportSkipped's capture-immune channel.</summary>
    private static void ReportSkip(string reason)
    {
        var line = $"⏭️ real-github pack E2E NOT EVALUATED — {reason}. A skip is not a pass: nothing was cloned, so nothing was verified.";
        var stepSummary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");

        if (!string.IsNullOrWhiteSpace(stepSummary)) System.IO.File.AppendAllText(stepSummary, line + Environment.NewLine);
        else Console.WriteLine(line);
    }
}

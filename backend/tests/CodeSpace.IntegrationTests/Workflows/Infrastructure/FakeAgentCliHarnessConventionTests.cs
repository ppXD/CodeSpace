using System.Text.RegularExpressions;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// The teeth behind <see cref="FakeAgentCliDialect"/>: a fake CLI that stands in for the agent on a LIVE-BRAIN arm has
/// to arm BOTH harness command env vars, or it stands in for nothing.
///
/// <para>A codex-only fake was sound while the supervisor's agent profile stayed on <c>codex-cli</c>. On the real-model
/// lanes it is not: the brain credential is Anthropic, so <c>HarnessModelReconciler</c> rewrites the authored
/// <c>codex-cli</c> to <c>claude-code</c> and the spawned agents resolve the REAL <c>claude</c> binary instead. Nothing
/// threw — the arm just quietly measured real CLI sessions against a fake's premises, and in run 33972713055 one of
/// those sessions wedged for the agent's full 1h default and killed the 120-min job.</para>
///
/// <para>The runtime half is guarded per-run by <c>RealModelGate.ClassifyHarnessControl</c>. This is the STATIC half:
/// it reds at build time when a NEW real-model arm reaches for a codex-only fake, instead of a live lane discovering it
/// months later. Scanned from SOURCE for the same reason its sibling convention tests are — constructing a fake would
/// arm the very process-wide env vars these files exist to police.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class FakeAgentCliHarnessConventionTests
{
    /// <summary>Matches a construction of any fake CLI, capturing the type name. Assembled from parts so this file is not itself a match for the sibling scan.</summary>
    private static readonly Regex ConstructsAFakeCli = new(@"\b" + "new" + @"\s+([A-Za-z0-9_]*Fake[A-Za-z0-9_]*Cli)\s*\(", RegexOptions.Compiled);

    [Fact]
    public void Every_fake_a_real_model_arm_reaches_for_arms_both_harness_command_env_vars()
    {
        var fakes = FakesUsedByRealModelArms();

        fakes.Count.ShouldBeGreaterThan(3, "the scan must actually find the fakes the real-model arms construct, or this test passes by finding nothing");

        var sources = fakes.ToDictionary(f => f, FakeSource, StringComparer.Ordinal);

        // A fake whose source this cannot READ is a fake this convention does not POLICE, and the old lookup answered
        // null for anything outside three hard-coded folders — so moving a fake one directory over (or adding one in a
        // new folder) silently exempted it from the very check that exists because the exemption is invisible. An
        // unreadable fake is now a RED, not a skip: the scan either sees every fake or says which one it lost.
        sources.Where(kv => kv.Value is null).Select(kv => kv.Key).OrderBy(f => f, StringComparer.Ordinal).ToList().ShouldBeEmpty(
            "a fake a REAL-MODEL arm constructs must have a locatable source file named after its type under backend/tests — "
          + "this convention can only police what it can read, and an unlocatable fake would pass by being invisible rather than by being correct");

        var offenders = sources
            .Where(kv => !(kv.Value!.Contains(nameof(CodexHarness) + "." + nameof(CodexHarness.CommandEnvVar), StringComparison.Ordinal)
                        && kv.Value!.Contains(nameof(ClaudeCodeHarness) + "." + nameof(ClaudeCodeHarness.CommandEnvVar), StringComparison.Ordinal)))
            .Select(kv => kv.Key)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "every fake a REAL-MODEL arm stands the agent up with must arm BOTH harness command env vars — HarnessModelReconciler rewrites the authored "
          + "codex-cli to claude-code whenever the brain credential is Anthropic, so a codex-only fake leaves the spawned agents running the REAL claude "
          + "binary and the arm measures something it never controlled. See FakeAgentCliDialect.");
    }

    [Fact]
    public void The_declared_stub_set_names_the_two_harness_kinds_a_dual_armed_fake_actually_covers()
    {
        // The set every live arm's control check is made against. If a third harness ever drives an Anthropic
        // credential, a fake arming only these two stops covering the reconciler's choices — red here, not in a lane.
        FakeAgentCliDialect.BothHarnessKinds.ShouldBe(new[] { "codex-cli", "claude-code" });
    }

    [Fact]
    public void An_unarmed_process_declares_no_stubbed_harness_kinds()
    {
        // The real-coding arm's premise: with no fake armed the derivation is EMPTY, so the control check self-disables
        // rather than red-ing an arm that legitimately expects the real binary. Guarded here because a derivation that
        // answered non-empty on a bare process would fail every real-CLI arm the moment it landed.
        if (FakeAgentCliMarker.IsFakeCli(Environment.GetEnvironmentVariable(CodexHarness.CommandEnvVar))
         || FakeAgentCliMarker.IsFakeCli(Environment.GetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar))) return;   // a sibling collection has one armed — nothing to assert

        FakeAgentCliDialect.ArmedFakeHarnessKinds().ShouldBeEmpty("no fake armed ⇒ no stubbed kinds ⇒ the control check must stay silent for a real-CLI arm");
    }

    /// <summary>The distinct fake-CLI type names constructed by any <c>RealModel*</c> test class across both test assemblies.</summary>
    private static IReadOnlyList<string> FakesUsedByRealModelArms() =>
        TestSourceRoots()
            .SelectMany(d => d.GetFiles("RealModel*.cs", SearchOption.AllDirectories))
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => ConstructsAFakeCli.Matches(File.ReadAllText(f.FullName)).Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>The source of a fake by type name, searched across BOTH test assemblies (one file per type, named after it — the repo's convention). Null ONLY when no such file exists anywhere, which the caller REDS on rather than treating as "nothing to police".</summary>
    private static string? FakeSource(string typeName) =>
        TestSourceRoots()
            .SelectMany(d => d.GetFiles(typeName + ".cs", SearchOption.AllDirectories))
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => File.ReadAllText(f.FullName))
            .FirstOrDefault();

    private static IEnumerable<DirectoryInfo> TestSourceRoots() =>
        new[] { "backend/tests/CodeSpace.E2ETests", "backend/tests/CodeSpace.IntegrationTests" }
            .Select(rel => new DirectoryInfo(Path.Combine(FindRepoRoot(), rel)))
            .Where(d => d.Exists);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}

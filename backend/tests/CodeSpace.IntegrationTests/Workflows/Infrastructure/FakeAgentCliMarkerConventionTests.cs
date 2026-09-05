using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// The teeth behind <see cref="FakeAgentCliMarker"/>. The real-CLI gates now self-skip when the command env var
/// points at a path carrying the fake markers — which is only sound while EVERY fake that arms that env var actually
/// carries them. A new fake that names its script something else would be invisible to those gates, and the hazard
/// would silently reopen: a real-CLI test would resolve the stub, run it, and assert real-binary semantics against it.
///
/// <para>Scanned from SOURCE rather than by constructing the fakes: every one of them arms a PROCESS-WIDE env var in
/// its constructor, so instantiating them here would race whatever else is running — the exact hazard this file
/// exists to close. Two of them also take constructor arguments.</para>
///
/// <para>Scans both test assemblies recursively by the <c>*FakeCli.cs</c> / <c>Fake*Cli.cs</c> naming convention,
/// then locates each by type name through <see cref="FakeCliSourceLocator"/> (shared with
/// <c>FakeAgentCliHarnessConventionTests</c>). A fixed, non-recursive two-folder list used to do this scan — it let a
/// fake staged anywhere else (e.g. <c>DecisionRaisingFakeCli</c>, which lives beside the flow it tests rather than
/// under an Infrastructure folder) pass this test by never being looked at, rather than by being examined and found
/// exempt.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class FakeAgentCliMarkerConventionTests
{
    /// <summary>The two file-naming shapes a fake CLI is known by: the common <c>...FakeCli.cs</c> suffix, and the older <c>Fake...Cli.cs</c> prefix (e.g. <c>FakeCodexCli</c>).</summary>
    private static readonly string[] FakeCliFileGlobs = { "*FakeCli.cs", "Fake*Cli.cs" };

    [Fact]
    public void Every_fake_cli_carries_the_markers_the_real_cli_gates_skip_on()
    {
        var typeNames = FakeCliTypeNames();

        var sources = typeNames.ToDictionary(name => name, FakeCliSourceLocator.SourceFor, StringComparer.Ordinal);

        // A fake named by the file convention but whose source this cannot RE-locate by type name is a fake this
        // convention does not POLICE — naming it here catches that as a RED instead of a silent drop.
        sources.Where(kv => kv.Value is null).Select(kv => kv.Key).OrderBy(f => f, StringComparer.Ordinal).ToList().ShouldBeEmpty(
            "a fake named by the *FakeCli.cs / Fake*Cli.cs convention must have a locatable source file under backend/tests — "
          + "this convention can only police what it can read, and an unlocatable fake would pass by being invisible rather than by being correct");

        sources.Count.ShouldBeGreaterThanOrEqualTo(18,
            "the census of known fakes must not shrink — a scan that regresses to a narrower folder list would silently drop coverage instead of reding");

        // Only a fake that ARMS one of the two harness command env vars can ever be resolved by a real-CLI gate in
        // the first place — the hazard this file exists to close is specifically "the gate reads
        // CodexHarness/ClaudeCodeHarness.CommandEnvVar and finds a fake's path". DecisionRaisingFakeCli is driven a
        // different way (a "scripted" IAgentHarness with a hardcoded /bin/sh invocation, never touching either var),
        // so it cannot be mistaken for a real binary by any such gate and does not need these markers.
        var offenders = sources
            .Where(kv => kv.Value!.Contains(nameof(CodexHarness) + "." + nameof(CodexHarness.CommandEnvVar), StringComparison.Ordinal)
                      || kv.Value!.Contains(nameof(ClaudeCodeHarness) + "." + nameof(ClaudeCodeHarness.CommandEnvVar), StringComparison.Ordinal))
            .Where(kv => !kv.Value!.Contains($"\"{FakeAgentCliMarker.ScriptNamePrefix}", StringComparison.Ordinal)
                      || !kv.Value!.Contains(FakeAgentCliMarker.DirectoryMarker, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            $"every fake that arms CodexHarness/ClaudeCodeHarness.CommandEnvVar must write a script named '{FakeAgentCliMarker.ScriptNamePrefix}…' inside a temp dir carrying '{FakeAgentCliMarker.DirectoryMarker}', "
          + "or the real-CLI gates cannot tell it from a real binary and will run it while asserting real-binary semantics");
    }

    [Theory]
    [InlineData("/tmp/cs-dephandoff-fakecli-abc123/fake-agent.sh", true)]
    [InlineData("/tmp/cs-filewriting-fakecli-def456/fake-codex.sh", true)]
    [InlineData("/usr/local/bin/claude", false)]
    [InlineData("/opt/homebrew/bin/codex", false)]
    [InlineData("/Users/someone/.local/bin/claude", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void A_real_binary_path_is_never_mistaken_for_a_fake(string? path, bool expectFake)
    {
        FakeAgentCliMarker.IsFakeCli(path).ShouldBe(expectFake,
            "a false positive here would make a real-CLI test skip forever, which is a silent loss of coverage — the opposite failure of the one this closes");
    }

    /// <summary>Every fake-CLI type name by the repo's file-naming convention, scanned recursively across both test
    /// assemblies (<see cref="FakeCliSourceLocator.TestSourceRoots"/>) rather than a fixed folder list, so a fake
    /// staged anywhere else — a new Infrastructure folder, or beside the flow it tests — is still found.</summary>
    private static IReadOnlyList<string> FakeCliTypeNames() =>
        FakeCliFileGlobs
            .SelectMany(glob => FakeCliSourceLocator.TestSourceRoots().SelectMany(d => d.GetFiles(glob, SearchOption.AllDirectories)))
            .Where(f => !FakeCliSourceLocator.IsBuildOutput(f))
            .Select(f => Path.GetFileNameWithoutExtension(f.Name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
}

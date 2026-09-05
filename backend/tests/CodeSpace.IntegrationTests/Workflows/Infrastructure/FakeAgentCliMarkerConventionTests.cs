using CodeSpace.IntegrationTests.Agents;
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

    /// <summary>The only fake CLI exempt from the marker rules below: <see cref="DecisionRaisingFakeCli"/> is driven through a "scripted" <c>IAgentHarness</c> with a hardcoded <c>/bin/sh</c> invocation and never touches either harness's <c>CommandEnvVar</c>, so no real-CLI gate can ever resolve it as a fake binary. Named here by type rather than left to a text-content filter, so a fake that arms the var by indirection cannot slip out of the check unnoticed.</summary>
    private static readonly string[] MarkerExemptTypeNames = { nameof(DecisionRaisingFakeCli) };

    [SkippableFact]
    public void Every_fake_cli_carries_the_markers_the_real_cli_gates_skip_on()
    {
        Skip.If(!FakeCliSourceLocator.RepoRootFound(), "the backend source tree is not alongside the test binaries (e.g. a published/copied test run) — this convention can only police what it can read");

        var typeNames = FakeCliTypeNames();

        var sources = typeNames.ToDictionary(name => name, FakeCliSourceLocator.SourceFor, StringComparer.Ordinal);

        // A fake named by the file convention but whose source this cannot RE-locate by type name is a fake this
        // convention does not POLICE — naming it here catches that as a RED instead of a silent drop.
        sources.Where(kv => kv.Value is null).Select(kv => kv.Key).OrderBy(f => f, StringComparer.Ordinal).ToList().ShouldBeEmpty(
            "a fake named by the *FakeCli.cs / Fake*Cli.cs convention must have a locatable source file under backend/tests — "
          + "this convention can only police what it can read, and an unlocatable fake would pass by being invisible rather than by being correct");

        // Exact, not a floor: a hardcoded ">= 18" still passes if the census silently shrinks by one (e.g. losing
        // DecisionRaisingFakeCli) — tying it to the scan's own count catches that instead of masking it.
        sources.Count.ShouldBe(typeNames.Count,
            "every fake-CLI type the naming convention enumerates must resolve to exactly one located source — the census comes from the scan itself, not a hardcoded floor that can go stale");

        sources.ShouldContainKey(nameof(DecisionRaisingFakeCli),
            "DecisionRaisingFakeCli is the one named exemption from the marker rules below — if the scan ever stops finding it, the exemption list must be revisited rather than silently drifting");

        // Every located fake except the named exemptions above must carry the markers, unconditionally — narrowing
        // to sources whose TEXT literally contains "CodexHarness.CommandEnvVar" would let a fake that arms the var
        // by indirection (a shared helper, a renamed local, a base-class field) skip enforcement invisibly instead
        // of being checked and found exempt.
        var offenders = sources
            .Where(kv => !MarkerExemptTypeNames.Contains(kv.Key, StringComparer.Ordinal))
            .Where(kv => !kv.Value!.Contains($"\"{FakeAgentCliMarker.ScriptNamePrefix}", StringComparison.Ordinal)
                      || !kv.Value!.Contains(FakeAgentCliMarker.DirectoryMarker, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            $"every fake CLI other than the named exemptions must write a script named '{FakeAgentCliMarker.ScriptNamePrefix}…' inside a temp dir carrying '{FakeAgentCliMarker.DirectoryMarker}', "
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

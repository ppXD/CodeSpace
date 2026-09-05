using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The cross-stack DRIFT DETECTOR for the network-posture sentence (Rule 12.5's shape, applied to copy instead of a
/// script). <c>AgentAutonomyPolicy.DescribeNetwork</c> authors the words a run's journal carries; the Launch composer
/// has to state the SAME posture BEFORE a run exists, so it cannot read them off the wire and necessarily duplicates
/// them. A duplicate nobody pins is a promise that quietly rots: the backend gets a caveat, the composer keeps the
/// old sentence, and the operator reads a posture the run never had — the exact dishonesty this whole row exists to end.
///
/// <para>So both stacks assert on ONE committed fixture: this test proves the fixture matches what the backend
/// actually says, and <c>frontend/src/lib/launchInput.test.ts</c> proves the composer's mirror says the same. Neither
/// side can move alone — changing a word is a two-file, two-test decision.</para>
/// </summary>
[Trait("Category", "Unit")]
public class NetworkPostureWordingDriftTests
{
    private const string FixturePath = "frontend/src/lib/networkPosture.fixture.json";

    private sealed record PostureCase(string Effective, string Ceiling, string Deployment, string Line);

    private sealed record ConfinedPostureCase(string Effective, string Ceiling, string Deployment, SandboxConfinement Confinement, string Line);

    /// <summary>The fixture's confinement blocks are read with the SAME options the executor persists the column with, so a fixture that parses here parses off a real row too.</summary>
    private static readonly JsonSerializerOptions FixtureJson = CodeSpace.Core.Services.Agents.AgentJson.Options;

    [Fact]
    public void The_shared_fixture_says_exactly_what_DescribeNetwork_says()
    {
        var cases = ReadFixture();

        cases.Count.ShouldBeGreaterThan(3, $"{FixturePath} must cover all four posture states (on / off / clamped off by policy / clamped off by deployment ceiling), or it pins nothing worth pinning");

        foreach (var (effective, ceiling, deployment, line) in cases)
        {
            var actual = AgentAutonomyPolicy.DescribeNetwork(
                AgentAutonomyPolicy.Parse(effective, AgentAutonomyLevel.Standard),
                AgentAutonomyPolicy.Parse(ceiling, AgentAutonomyLevel.Standard),
                AgentAutonomyPolicy.Parse(deployment, AgentAutonomyLevel.Standard));

            actual.ShouldBe(line,
                customMessage: $"the backend's '{effective}' under ceiling '{ceiling}' / deployment ceiling '{deployment}' sentence no longer matches {FixturePath} — update the fixture AND the frontend mirror in the same change, so the composer never predicts a posture the journal contradicts");
        }
    }

    [Fact]
    public void The_shared_fixture_says_exactly_what_a_RECORDED_posture_says()
    {
        // The composer can never produce these lines — it speaks before a run exists, so it has no record and always
        // falls back to a `cases` line. They are still pinned in the SHARED fixture so the frontend's own assertion
        // (a resolved sentence REPLACES the hedge, never appends to it) is checking the backend's real words.
        var cases = ReadConfinementFixture();

        cases.Count.ShouldBeGreaterThan(3, $"{FixturePath} confinementCases must cover confined/severed, confined/shared, unconfined and not-applicable, or it pins nothing worth pinning");

        foreach (var (effective, ceiling, deployment, confinement, line) in cases)
        {
            var actual = AgentAutonomyPolicy.DescribeNetwork(
                AgentAutonomyPolicy.Parse(effective, AgentAutonomyLevel.Standard),
                AgentAutonomyPolicy.Parse(ceiling, AgentAutonomyLevel.Standard),
                AgentAutonomyPolicy.Parse(deployment, AgentAutonomyLevel.Standard),
                confinement);

            actual.ShouldBe(line,
                customMessage: $"the backend's '{effective}'/'{ceiling}' sentence for a {confinement.Outcome} run no longer matches {FixturePath} — update the fixture in the same change, so the frontend's replacement assertion keeps checking real words");
        }
    }

    [Fact]
    public void The_shared_fixture_covers_every_unconfinable_reason()
    {
        // A fixture that only sampled one reason would let the other two drift into an unhelpful "unavailable". The
        // reason IS the actionable half of an unconfined verdict ("install bwrap" vs "allow user namespaces").
        var lines = ReadConfinementFixture().Select(c => c.Line).ToList();

        foreach (var reason in new[] { SandboxConfinement.ReasonNotLinux, SandboxConfinement.ReasonNoBubblewrap, SandboxConfinement.ReasonNoUserNamespaces })
            lines.ShouldContain(l => l.Contains($"({reason})"), $"no case pins the '{reason}' wording");
    }

    [Fact]
    public void The_shared_fixture_covers_every_posture_state()
    {
        // A fixture that only sampled "on" would let the qualified "off" wordings drift freely. Assert the coverage
        // itself, not just the rows that happen to be there.
        var lines = ReadFixture().Select(c => c.Line).ToList();

        lines.ShouldContain(l => l.StartsWith("Network: on ("), "no 'on' case");
        lines.ShouldContain(l => l.StartsWith("Network: off ("), "no 'off' case");
        lines.ShouldContain(l => l.StartsWith("Network: clamped off by policy ("), "no 'clamped off by policy' case");
        lines.ShouldContain(l => l.StartsWith("Network: clamped off by deployment ceiling ("), "no 'clamped off by deployment ceiling' case");
    }

    private static IReadOnlyList<ConfinedPostureCase> ReadConfinementFixture()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepoRoot(), FixturePath)));

        return document.RootElement.GetProperty("confinementCases").EnumerateArray()
            .Select(c => new ConfinedPostureCase(
                c.GetProperty("effective").GetString()!,
                c.GetProperty("ceiling").GetString()!,
                c.GetProperty("deployment").GetString()!,
                c.GetProperty("confinement").Deserialize<SandboxConfinement>(FixtureJson)!,
                c.GetProperty("line").GetString()!))
            .ToList();
    }

    private static IReadOnlyList<PostureCase> ReadFixture()
    {
        var path = Path.Combine(FindRepoRoot(), FixturePath);

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        return document.RootElement.GetProperty("cases").EnumerateArray()
            .Select(c => new PostureCase(c.GetProperty("effective").GetString()!, c.GetProperty("ceiling").GetString()!, c.GetProperty("deployment").GetString()!, c.GetProperty("line").GetString()!))
            .ToList();
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "backend"))) return dir.FullName;

        throw new InvalidOperationException($"repo root not found walking up from {AppContext.BaseDirectory}");
    }
}

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

    private sealed record PostureCase(string Effective, string Ceiling, string Line);

    [Fact]
    public void The_shared_fixture_says_exactly_what_DescribeNetwork_says()
    {
        var cases = ReadFixture();

        cases.Count.ShouldBeGreaterThan(2, $"{FixturePath} must cover all three posture states (on / off / clamped off), or it pins nothing worth pinning");

        foreach (var (effective, ceiling, line) in cases)
        {
            var actual = AgentAutonomyPolicy.DescribeNetwork(
                AgentAutonomyPolicy.Parse(effective, AgentAutonomyLevel.Standard),
                AgentAutonomyPolicy.Parse(ceiling, AgentAutonomyLevel.Standard));

            actual.ShouldBe(line,
                customMessage: $"the backend's '{effective}' under ceiling '{ceiling}' sentence no longer matches {FixturePath} — update the fixture AND the frontend mirror in the same change, so the composer never predicts a posture the journal contradicts");
        }
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
    }

    private static IReadOnlyList<PostureCase> ReadFixture()
    {
        var path = Path.Combine(FindRepoRoot(), FixturePath);

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        return document.RootElement.GetProperty("cases").EnumerateArray()
            .Select(c => new PostureCase(c.GetProperty("effective").GetString()!, c.GetProperty("ceiling").GetString()!, c.GetProperty("line").GetString()!))
            .ToList();
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "backend"))) return dir.FullName;

        throw new InvalidOperationException($"repo root not found walking up from {AppContext.BaseDirectory}");
    }
}

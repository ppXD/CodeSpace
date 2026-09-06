using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins each harness's <c>DefaultVersion</c> constant to the SINGLE SOURCE OF TRUTH — the
/// <c>CODEX_CLI_VERSION</c> / <c>CLAUDE_CODE_VERSION</c> ARG in <c>backend/Dockerfile.worker</c> (the version the
/// worker image actually installs). A bump in the Dockerfile that isn't mirrored into the C# constant (or vice
/// versa) FAILS here, so the harness-reported version can never silently drift from what the worker runs. The third
/// surface — a developer's local install — is synced from the same ARG by <c>deploy/sync-local-harnesses.sh</c>. The
/// fourth is <c>.github/workflows/real-model.yml</c>'s EXACT-pinned installs, the lanes that verify the CLI surface
/// the harness argv targets; they must name the shipped version or the gate certifies a CLI nobody runs.
/// </summary>
[Trait("Category", "Unit")]
public class HarnessVersionPinTests
{
    [Fact]
    public void Codex_default_version_matches_the_worker_dockerfile_pin() =>
        DockerfileArg("CODEX_CLI_VERSION").ShouldBe(CodexHarness.DefaultVersion);

    [Fact]
    public void Claude_default_version_matches_the_worker_dockerfile_pin() =>
        DockerfileArg("CLAUDE_CODE_VERSION").ShouldBe(ClaudeCodeHarness.DefaultVersion);

    /// <summary>
    /// The FOURTH surface: <c>real-model.yml</c>'s exact-pinned installs — the lanes whose whole job is to verify the
    /// CLI surface the harness argv targets. An exact pin there that lags the worker's ARG means the gate certifies a
    /// version the product does not ship, which is precisely the silent drift the other pins exist to prevent. Only
    /// EXACT pins are asserted; the deliberately FLOATING <c>@~2.1.0</c> lanes are excluded by the version pattern, so
    /// they keep tracking the newest 2.1.x without failing here.
    /// </summary>
    [Fact]
    public void Claude_exact_workflow_pins_match_the_worker_dockerfile_pin()
    {
        var pinned = DockerfileArg("CLAUDE_CODE_VERSION");
        var matches = Regex.Matches(File.ReadAllText(LocateRealModelWorkflow()), @"@anthropic-ai/claude-code@(\d+\.\d+\.\d+)");

        matches.Count.ShouldBeGreaterThan(0, "real-model.yml must keep at least one exact-pinned claude-code install — the lane that verifies the CLI surface");

        foreach (var version in matches.Select(m => m.Groups[1].Value).Distinct())
            version.ShouldBe(pinned, $"an exact '@anthropic-ai/claude-code@{version}' install in .github/workflows/real-model.yml lags CLAUDE_CODE_VERSION in backend/Dockerfile.worker — the lane would certify a CLI the worker image never installs");
    }

    private static string DockerfileArg(string name)
    {
        var content = File.ReadAllText(LocateWorkerDockerfile());
        var match = Regex.Match(content, $@"ARG\s+{Regex.Escape(name)}=(\S+)");

        match.Success.ShouldBeTrue($"ARG {name} not found in backend/Dockerfile.worker");
        return match.Groups[1].Value;
    }

    private static string LocateWorkerDockerfile() => LocateRepoFile("backend", "Dockerfile.worker");

    private static string LocateRealModelWorkflow() => LocateRepoFile(".github", "workflows", "real-model.yml");

    private static string LocateRepoFile(params string[] segments)
    {
        var relative = Path.Combine(segments);

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException($"{relative} not found walking up from {AppContext.BaseDirectory}");
    }
}

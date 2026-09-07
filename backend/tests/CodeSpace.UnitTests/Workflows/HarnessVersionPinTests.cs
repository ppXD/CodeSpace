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
/// fourth is every install in <c>.github/workflows/real-model.yml</c>, the lanes that verify the CLI surface
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
    /// Every qualification lane, including benchmark and whole-loop workers, must exercise the version shipped in
    /// the worker image. Inspect all install selectors: filtering to exact versions would silently exempt floating
    /// ranges and certify a different binary. Provider or CLI drift belongs in a separately identified canary.
    /// </summary>
    [Theory]
    [InlineData("@anthropic-ai/claude-code", "CLAUDE_CODE_VERSION")]
    [InlineData("@openai/codex", "CODEX_CLI_VERSION")]
    public void Every_qualification_workflow_install_matches_the_worker_dockerfile_pin(string package, string versionArgument)
    {
        var pinned = DockerfileArg(versionArgument);
        var installs = string.Join('\n', File.ReadLines(LocateRealModelWorkflow()).Where(line => !line.TrimStart().StartsWith('#') && line.Contains("npm install", StringComparison.Ordinal)));
        var matches = Regex.Matches(installs, Regex.Escape(package) + "(?:@([^\\s'\";|]+))?");

        matches.Count.ShouldBeGreaterThan(0, $"real-model.yml must install {package} to verify the real CLI surface");

        foreach (var version in matches.Select(m => m.Groups[1].Value).Distinct())
            version.ShouldBe(pinned, $"'{package}@{version}' in real-model.yml must match {versionArgument} in backend/Dockerfile.worker; a floating or stale selector certifies a different binary");
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

using System;
using System.IO;
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
/// surface — a developer's local install — is synced from the same ARG by <c>deploy/sync-local-harnesses.sh</c>.
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

    private static string DockerfileArg(string name)
    {
        var content = File.ReadAllText(LocateWorkerDockerfile());
        var match = Regex.Match(content, $@"ARG\s+{Regex.Escape(name)}=(\S+)");

        match.Success.ShouldBeTrue($"ARG {name} not found in backend/Dockerfile.worker");
        return match.Groups[1].Value;
    }

    private static string LocateWorkerDockerfile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "backend", "Dockerfile.worker");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("backend/Dockerfile.worker not found walking up from " + AppContext.BaseDirectory);
    }
}

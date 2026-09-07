using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using Shouldly;
using YamlDotNet.RepresentationModel;

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

    [Fact]
    public void Every_cli_qualification_job_sets_up_the_worker_node_major_before_installing()
    {
        var worker = Regex.Match(File.ReadAllText(LocateWorkerDockerfile()), @"(?m)^FROM node:(\d+)[^\r\n]* AS agent-cli$");
        worker.Success.ShouldBeTrue("the worker CLI runtime must declare its Node major");

        foreach (var (job, steps) in CliJobSteps())
        {
            string? nodeVersion = null;
            foreach (var step in steps)
            {
                if (Value(step, "uses")?.StartsWith("actions/setup-node@", StringComparison.Ordinal) == true)
                    nodeVersion = Value((YamlMappingNode)step.Children[new YamlScalarNode("with")], "node-version");

                if (IsCliInstall(Value(step, "run")))
                    nodeVersion.ShouldBe(worker.Groups[1].Value, $"{job}: qualification must install under the same Node major as the worker; missing/later setup-node does not qualify an install");
            }
        }
    }

    [Fact]
    public void Every_cli_install_and_retry_enforces_the_packages_declared_engine_compatibility()
    {
        foreach (var (job, steps) in CliJobSteps())
        foreach (var step in steps.Where(step => IsCliInstall(Value(step, "run"))))
        {
            Value(step, "continue-on-error").ShouldNotBe("true", $"{job}: an incompatible CLI must stop the lane before live tests");
            var commands = Regex.Matches(Value(step, "run")!, @"\bnpm\s+install\b[^;\r\n|}]*");
            commands.Count.ShouldBeGreaterThan(0);
            foreach (Match command in commands)
                Regex.IsMatch(command.Value, @"(?:^|\s)--engine-strict(?:\s|$)").ShouldBeTrue($"{job}: each install, including a registry retry, must fail on incompatible package engines instead of emitting EBADENGINE and continuing");
        }
    }

    [Fact]
    public void Every_cli_install_records_the_actual_node_and_npm_runtime_before_installing()
    {
        foreach (var (job, steps) in CliJobSteps())
        foreach (var step in steps.Where(step => IsCliInstall(Value(step, "run"))))
        {
            var script = Value(step, "run")!;
            var install = script.IndexOf("npm install", StringComparison.Ordinal);
            var preflight = script[..install];
            Regex.IsMatch(preflight, @"(?m)^\s*node --version\s*$").ShouldBeTrue($"{job}: report the actual runtime, not only the setup-node selector");
            Regex.IsMatch(preflight, @"(?m)^\s*npm --version\s*$").ShouldBeTrue($"{job}: record the npm version enforcing engines");
        }
    }

    private static IReadOnlyList<(string Job, YamlMappingNode[] Steps)> CliJobSteps()
    {
        using var reader = File.OpenText(LocateRealModelWorkflow());
        var yaml = new YamlStream();
        yaml.Load(reader);
        var root = (YamlMappingNode)yaml.Documents.Single().RootNode;
        var jobs = (YamlMappingNode)root.Children[new YamlScalarNode("jobs")];
        var result = new List<(string, YamlMappingNode[])>();
        foreach (var (name, definition) in jobs.Children)
        {
            var job = (YamlMappingNode)definition;
            if (!job.Children.TryGetValue(new YamlScalarNode("steps"), out var steps)) continue;
            var sequence = ((YamlSequenceNode)steps).Children.Cast<YamlMappingNode>().ToArray();
            if (sequence.Any(step => IsCliInstall(Value(step, "run")))) result.Add((((YamlScalarNode)name).Value!, sequence));
        }

        result.Count.ShouldBeGreaterThan(0, "the qualification census must include real CLI jobs");
        return result;
    }

    private static bool IsCliInstall(string? script) => script is not null && script.Contains("npm install", StringComparison.Ordinal) && (script.Contains("@anthropic-ai/claude-code", StringComparison.Ordinal) || script.Contains("@openai/codex", StringComparison.Ordinal));

    private static string? Value(YamlMappingNode mapping, string key) => mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) ? ((YamlScalarNode)value).Value : null;

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

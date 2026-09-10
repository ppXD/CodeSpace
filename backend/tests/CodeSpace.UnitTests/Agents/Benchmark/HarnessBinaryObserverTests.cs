using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents.Benchmark;

/// <summary>
/// The observer freezes BYTES, not a self-reported version. These pin the answers a campaign can get — the real
/// digest, a resolvable binary that was swapped underneath, and an absent one — plus the fact that both shipped
/// harnesses are observable at all (a harness that shells out but declares no binary would be silently unfrozen).
/// </summary>
[Trait("Category", "Unit")]
public sealed class HarnessBinaryObserverTests : IDisposable
{
    private const string RealScript = "#!/bin/sh\necho real\n";
    private const string PathScript = "#!/bin/sh\necho on-path\n";

    private readonly string _root = Directory.CreateTempSubdirectory($"harness-observe-{Guid.NewGuid():N}").FullName;

    [Fact]
    public void An_absolute_command_is_hashed_from_its_actual_bytes()
    {
        var identity = HarnessBinaryObserver.Observe("claude-code", "2.1.263", Stage("claude", RealScript));

        identity.Kind.ShouldBe("claude-code");
        identity.Version.ShouldBe("2.1.263");
        identity.UnobservedReason.ShouldBeNull();
        identity.BinarySha256.ShouldBe(Sha256Of(RealScript));
    }

    [Fact]
    public void A_swapped_binary_at_the_same_path_and_version_observes_a_different_digest()
    {
        var path = Stage("claude", RealScript);
        var original = HarnessBinaryObserver.Observe("claude-code", "2.1.263", path);

        File.WriteAllText(path, "#!/bin/sh\necho swapped\n");

        HarnessBinaryObserver.Observe("claude-code", "2.1.263", path).BinarySha256
            .ShouldNotBe(original.BinarySha256, "an unchanged version string is exactly the substitution the digest exists to catch");
    }

    [Theory]
    [InlineData("codex-absent-from-this-host")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_command_that_resolves_to_nothing_is_frozen_as_an_absence(string command)
    {
        var identity = HarnessBinaryObserver.Observe("codex-cli", "0.142.2", command);

        identity.BinarySha256.ShouldBeNull();
        identity.UnobservedReason.ShouldBe(HarnessBinaryIdentity.ReasonNotFound);
    }

    [Fact]
    public void A_directory_qualified_command_is_never_searched_on_path()
    {
        Stage("codex", PathScript);

        HarnessBinaryObserver.Observe("codex-cli", "0.142.2", Path.Combine(_root, "nested", "codex"), _root).UnobservedReason
            .ShouldBe(HarnessBinaryIdentity.ReasonNotFound, "a path-qualified command must not silently fall back to a same-named binary elsewhere on PATH");
    }

    [Fact]
    public void A_bare_name_resolves_across_path_exactly_as_the_os_would()
    {
        Stage("codex", PathScript);
        var search = $"{Path.Combine(_root, "does-not-exist")}{Path.PathSeparator}{_root}";

        HarnessBinaryObserver.ResolveOnPath("codex", search).ShouldBe(Path.Combine(_root, "codex"));
        HarnessBinaryObserver.Observe("codex-cli", "0.142.2", "codex", search).BinarySha256.ShouldBe(Sha256Of(PathScript));
    }

    [Fact]
    public void Only_harnesses_that_drive_a_binary_are_observed_and_they_come_back_ordered_by_kind()
    {
        var path = Stage("claude", RealScript);
        var harnesses = new IAgentHarness[] { new StubBinaryHarness("z-harness", path), new StubHarness("a-in-process"), new StubBinaryHarness("b-harness", path) };

        HarnessBinaryObserver.Observe(harnesses).Select(identity => identity.Kind).ShouldBe(new[] { "b-harness", "z-harness" });
    }

    [Fact]
    public void Both_shipped_harnesses_can_name_the_binary_they_drive()
    {
        typeof(ClaudeCodeHarness).IsAssignableTo(typeof(IAgentHarnessBinary)).ShouldBeTrue($"{nameof(ClaudeCodeHarness)} shells out to a CLI, so a campaign must be able to freeze its bytes");
        typeof(CodexHarness).IsAssignableTo(typeof(IAgentHarnessBinary)).ShouldBeTrue($"{nameof(CodexHarness)} shells out to a CLI, so a campaign must be able to freeze its bytes");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Stage(string name, string content)
    {
        var path = Path.Combine(_root, name);

        File.WriteAllText(path, content);

        return path;
    }

    private static string Sha256Of(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private sealed class StubHarness : IAgentHarness
    {
        public StubHarness(string kind) => Kind = kind;

        public string Kind { get; }
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "m" };

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "x" };
        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => Array.Empty<AgentEvent>();
        public IAgentEventFolder CreateFolder() => new TestEventFolder((fold, exitCode) => new() { Status = AgentRunStatus.Succeeded, ExitReason = "completed" });
    }

    private sealed class StubBinaryHarness : IAgentHarness, IAgentHarnessBinary
    {
        private readonly string _command;

        public StubBinaryHarness(string kind, string command)
        {
            Kind = kind;
            _command = command;
        }

        public string Kind { get; }
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "m" };

        public string ResolveCommand() => _command;
        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = _command };
        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => Array.Empty<AgentEvent>();
        public IAgentEventFolder CreateFolder() => new TestEventFolder((fold, exitCode) => new() { Status = AgentRunStatus.Succeeded, ExitReason = "completed" });
    }
}

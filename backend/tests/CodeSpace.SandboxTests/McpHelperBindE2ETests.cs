using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Authority;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.SandboxTests;

/// <summary>
/// 🟢 Sandbox E2E (high fidelity, Rule 12): what a confined agent can see of the <c>codespace-mcp</c> helper and of the
/// socket it reaches its tool fabric through. A REAL durable <see cref="LocalProcessRunner"/> launch (its own
/// <c>PlanFor</c> → <see cref="BubblewrapSandbox.BuildArgs"/> → bwrap) runs the REAL built helper, from where
/// <see cref="LocalProcessRunner.McpProxyBinaryPath"/> resolves it by default, against a REAL
/// <see cref="AgentMcpEndpoint"/> bound at the path <see cref="LocalProcessRunner.McpSocketPathFor"/> lays out. The
/// helper's directory is this test bin, which like the worker's <c>/app</c> holds the host's own assemblies — plus an
/// <c>appsettings</c> file with a secret in it, staged for the test. Runs for real only where bwrap confines (the
/// <c>sandbox-isolation.yml</c> privileged container); elsewhere it takes the honest skip. POSIX-only.
/// </summary>
[Trait("Category", "Sandbox")]
public sealed class McpHelperBindE2ETests
{
    private const string InitializeRequest = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";

    [Fact]
    public async Task The_real_proxy_authenticates_through_a_read_only_socket_directory_and_sees_only_its_own_files()
    {
        if (BubblewrapSandbox.Available is null)
        {
            BubblewrapSandbox.IsRequired.ShouldBeFalse("Sandbox:RequireConfinement is set but this host cannot sandbox (bwrap/userns) — the E2E cannot prove the helper's binds here");
            return;
        }

        await using var ctx = new McpHelperBindTestContext();

        var (result, lines) = await ctx.LaunchAndCollectAsync(ProbeScript);
        var output = string.Join("\n", lines);

        result.Status.ShouldBe(SandboxStatus.Success, $"the confined probe must run to a clean exit (exit {result.ExitCode}): {output} {result.Stderr}");

        var hidden = $"a file beside the helper (an appsettings holding a secret, as /app/appsettings.json holds the JWT key and the DB password) must not exist inside the sandbox. Output: {output}";
        Tagged(lines, "bait:").ShouldHaveSingleItem(hidden).ShouldContain("No such file or directory", customMessage: hidden);
        output.ShouldNotContain(ctx.BaitSecret, customMessage: "the secret beside the helper reached the agent");

        Tagged(lines, "ls:").Order(StringComparer.Ordinal).ShouldBe(ctx.HelperFileNames.Order(StringComparer.Ordinal), customMessage: $"inside the sandbox the helper's directory must hold its own files and nothing else — a listing of the host's app dir means its directory was bound whole. Output: {output}");

        var reached = $"the proxy must start from its own files alone, connect through the read-only directory, authenticate, and carry the endpoint's initialize reply back. Output: {output}";
        Tagged(lines, "proxy-exit:").ShouldHaveSingleItem(reached).ShouldBe("0", reached);
        Tagged(lines, "reply:").ShouldHaveSingleItem(reached).ShouldContain("\"serverInfo\"", customMessage: reached);
        ctx.Endpoint.HandshakeObserved.ShouldBeTrue("the real endpoint must have accepted the proxy's token and served initialize — the host-side proof it was reached and authenticated");

        var unlinked = $"unlinking the socket from inside must fail as EROFS — the kernel's refusal of a read-only mount. Output: {output}";
        Tagged(lines, "unlink:").ShouldHaveSingleItem(unlinked).ShouldContain("Read-only file system", customMessage: unlinked);

        var planted = $"planting a file beside the socket must fail as EROFS. Output: {output}";
        Tagged(lines, "plant:").ShouldHaveSingleItem(planted).ShouldContain("Read-only file system", customMessage: planted);

        File.Exists(ctx.SocketPath).ShouldBeTrue($"the socket must survive the agent's attempt to unlink it — check `ls -la {Path.GetDirectoryName(ctx.SocketPath)}`");
        File.Exists(Path.Combine(Path.GetDirectoryName(ctx.SocketPath)!, "planted")).ShouldBeFalse("nothing written inside may land beside the host's socket");
    }

    /// <summary>
    /// Runs inside the sandbox and reports tagged lines: the helper directory's listing, a read of the staged secret,
    /// one MCP <c>initialize</c> through the real proxy, then an unlink of the socket and a plant beside it. The proxy's
    /// stdin stays open until its reply has arrived (bounded at 15s), because it stops forwarding at the first EOF.
    /// </summary>
    private const string ProbeScript = """
        for f in "$HELPER_DIR"/*; do [ -e "$f" ] && printf 'ls:%s\n' "${f##*/}"; done
        cat "$BAIT" 2>&1 | sed 's/^/bait:/'
        : > /tmp/reply
        ( printf '%s\n' "$REQUEST"; i=0; while [ "$(wc -l < /tmp/reply)" -lt 1 ] && [ "$i" -lt 150 ]; do sleep 0.1; i=$((i+1)); done ) | "$PROXY" --proxy > /tmp/reply 2>&1
        printf 'proxy-exit:%s\n' "$?"
        sed 's/^/reply:/' /tmp/reply
        rm -f "$CODESPACE_MCP_SOCKET" 2>&1 | sed 's/^/unlink:/'
        touch "$SOCKET_DIR/planted" 2>&1 | sed 's/^/plant:/'
        """;

    private static List<string> Tagged(IEnumerable<string> lines, string tag) => lines.Where(l => l.StartsWith(tag, StringComparison.Ordinal)).Select(l => l[tag.Length..]).ToList();

    /// <summary>
    /// One run's staged world, every piece of it GUID-keyed (Rule 12.2) and torn down on dispose even when an assertion
    /// failed (Rule 12.3): the spool keyed by the run id as the executor keys it, the real endpoint on the run's socket
    /// path, the secret staged beside the helper, and the workspace.
    /// </summary>
    private sealed class McpHelperBindTestContext : IAsyncDisposable
    {
        private readonly LocalProcessRunner _runner = new();
        private readonly Guid _runId = Guid.NewGuid();
        private readonly string _token = McpRunToken.Mint();
        private readonly string _workspace = Path.Combine(Path.GetTempPath(), "cs-mcp-bind-ws-" + Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider _services = new ServiceCollection().AddSingleton<IAgentAuthorityCallGuard>(new NoAuthority()).BuildServiceProvider();

        public McpHelperBindTestContext()
        {
            SocketPath = LocalProcessRunner.McpSocketPathFor(_runId.ToString("N"), McpRunToken.MintPathId());
            Proxy = LocalProcessRunner.McpProxyBinaryPath();
            Bait = Path.Combine(Path.GetDirectoryName(Proxy)!, $"appsettings.{Guid.NewGuid():N}.json");

            File.Exists(Proxy).ShouldBeTrue($"the built codespace-mcp must sit at '{Proxy}' — the SandboxTests csproj's CopyMcpProxyToOutput target lands it there");

            Directory.CreateDirectory(_workspace);
            File.WriteAllText(Bait, $$"""{ "Jwt": { "Key": "{{BaitSecret}}" } }""");

            Endpoint = new AgentMcpEndpoint(_runId, new NoTools(), AgentAutonomyLevel.Standard, Guid.NewGuid(), SecretRedactor.None, SocketPath, _token, new AgentMcpConnectRegistry(), _services.CreateScope(), CancellationToken.None, NullLogger.Instance);
        }

        public string SocketPath { get; }

        public string Proxy { get; }

        public string Bait { get; }

        public string BaitSecret { get; } = "BAIT-SECRET-" + Guid.NewGuid().ToString("N");

        public AgentMcpEndpoint Endpoint { get; }

        public IEnumerable<string> HelperFileNames => LocalProcessRunner.McpProxyFiles(Proxy).Select(f => Path.GetFileName(f));

        public async Task<(SandboxResult Result, List<string> Lines)> LaunchAndCollectAsync(string script)
        {
            var handle = await _runner.LaunchAsync(SpecFor(script), _runId.ToString("N"), CancellationToken.None);

            var lines = new List<string>();
            var result = await _runner.AttachAsync(handle, (frame, _) => { lines.Add(frame.Text.Trim()); return Task.CompletedTask; }, CancellationToken.None);

            return (result, lines);
        }

        /// <summary>The launch the executor would make for an MCP-wired run: the rendered declaration in the config home, the socket on the wiring, and the proxy's two variables in the environment the CLI hands it.</summary>
        private SandboxSpec SpecFor(string script) => new()
        {
            Command = "/bin/sh",
            Args = new[] { "-c", script },
            WorkingDirectory = _workspace,
            ConfigHomeEnvVars = new[] { "CLAUDE_CONFIG_DIR" },
            TimeoutSeconds = 60,
            Mcp = new McpServerWiring
            {
                RelativeFileName = ".mcp.json",
                Content = McpDeclarationWriter.RenderClaudeJson(new McpDeclarationContext { ProxyCommand = Proxy, SocketPath = SocketPath, Token = _token, ServerName = McpRequestHandler.ServerName }),
                SocketPath = SocketPath,
            },
            Environment = new Dictionary<string, string>
            {
                [McpDeclarationWriter.SocketEnvVar] = SocketPath,
                [McpDeclarationWriter.TokenEnvVar] = _token,
                ["PROXY"] = Proxy,
                ["HELPER_DIR"] = Path.GetDirectoryName(Proxy)!,
                ["BAIT"] = Bait,
                ["SOCKET_DIR"] = Path.GetDirectoryName(SocketPath)!,
                ["REQUEST"] = InitializeRequest,
            },
        };

        public async ValueTask DisposeAsync()
        {
            await Endpoint.DisposeAsync();
            await _services.DisposeAsync();

            Quietly(() => File.Delete(Bait));
            Quietly(() => Directory.Delete(_workspace, recursive: true));
            Quietly(() => Directory.Delete(Path.GetDirectoryName(SocketPath)!, recursive: true));
            Quietly(() => Directory.Delete(LocalProcessRunner.SpoolDirectoryFor(_runId.ToString("N")), recursive: true));
        }

        private static void Quietly(Action action)
        {
            try { action(); } catch { /* best-effort cleanup */ }
        }
    }

    private sealed class NoTools : IAgentToolRegistry
    {
        public IReadOnlyList<IAgentTool> All { get; } = Array.Empty<IAgentTool>();

        public IAgentTool? Resolve(string kind) => null;
    }

    /// <summary>No tool is called here; initialize is served before any authority check, so a guard that refuses everything keeps the probe honest.</summary>
    private sealed class NoAuthority : IAgentAuthorityCallGuard
    {
        public Task<AuthorityCallFailure?> CheckAsync(Guid runId, Guid teamId, string toolKind, CancellationToken cancellationToken) => Task.FromResult<AuthorityCallFailure?>(new("agent.authority_denied", "mcp-bind-e2e", "No tool is called by this probe.", false));
    }
}

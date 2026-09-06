using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Mcp;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// A fake <c>claude</c> CLI that loads its MCP declaration THE WAY THE REAL ONE DOES: it reads <c>--mcp-config</c>
/// off its own argv, spawns the server that declaration names with the env it declares, and speaks one
/// <c>initialize</c> to it. Everything else about it is a no-op.
///
/// <para><b>Why it exists.</b> The declaration is written into <c>CLAUDE_CONFIG_DIR</c> while the CLI runs with cwd =
/// the workspace, and the real CLI only ever auto-discovers a project <c>.mcp.json</c> at the CWD. So a fake that
/// simply ignored argv would "pass" a fabric test exactly as a broken production argv "passes" a run: silently, with
/// no server ever loaded. This one is the CI-runnable arm of that leg — it fails the moment the harness stops naming
/// the declaration on the argv, which is the defect it was written for.</para>
///
/// <para><b>Fidelity.</b> Tier 🟡 medium-mock on the CLI itself (the proprietary binary cannot run in CI) and 🟢 on
/// everything under it: the declaration is the production one the harness rendered, the server it spawns is the REAL
/// <c>codespace-mcp</c> proxy binary, and the initialize crosses the REAL per-run UDS to the REAL endpoint. The
/// on-demand <c>CODESPACE_RUN_REAL_CLI_MCP_SMOKE</c> arm covers the real binary's own argv parsing.</para>
///
/// <para>Arms ONLY <see cref="ClaudeCodeHarness.CommandEnvVar"/>: this fake stands in for the one harness that must be
/// POINTED at its declaration. Codex reads <c>CODEX_HOME/config.toml</c> natively and needs no argv at all, so arming
/// it here would fake away the very difference under test. POSIX <c>/bin/sh</c> only (no bashisms).</para>
/// </summary>
public sealed class McpConfigLoadingFakeCli : IDisposable
{
    /// <summary>The summary the fake folds when the declared MCP server answered its <c>initialize</c> — the fabric is genuinely reachable from the CLI's own argv.</summary>
    public const string HandshakeSummary = "mcp-initialize-ok";

    /// <summary>The summary the fake folds when its argv named no declaration at all — today's production shape, and what this test suite must never see again.</summary>
    public const string NoConfigSummary = "mcp-config-absent";

    /// <summary>The summary the fake folds when it WAS pointed at a declaration but the server never answered — a live fabric fault, distinct from a missing flag.</summary>
    public const string NoReplySummary = "mcp-initialize-no-reply";

    /// <summary>Appended to the summary when the CWD holds NO <c>.mcp.json</c> — i.e. the CLI's own project-scope discovery had nothing to find and only the argv could have reached the declaration. The production shape, and the half the old on-demand smoke faked away by running with cwd = the config home.</summary>
    public const string CwdDeclarationAbsent = "cwd-declaration-absent";

    /// <summary>Appended instead when a <c>.mcp.json</c> DOES sit in the cwd — then a passing handshake proves nothing about the argv, because discovery alone could have produced it.</summary>
    public const string CwdDeclarationPresent = "cwd-declaration-present";

    private readonly string? _original;
    private readonly string _dir;

    public McpConfigLoadingFakeCli()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs-mcpconfig-fakecli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var script = Path.Combine(_dir, "fake-claude.sh");
        File.WriteAllText(script, ScriptBody);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        _original = Environment.GetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar);
        Environment.SetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar, script);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar, _original);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Walk argv for <c>--mcp-config</c> and take the NEXT token as the declaration path (the real CLI's own shape).
    /// Read the server's command, its single arg, and its socket/token env OUT OF THAT FILE — never from a constant
    /// here — so a harness that changes what it renders reds this rather than passing against a stale mirror
    /// (Rule 12.5). Then pipe one JSON-RPC <c>initialize</c> through it: the proxy sends the token, forwards the line,
    /// and prints the endpoint's reply, which <c>head -n 1</c> bounds so the pipeline always terminates. The trailing
    /// <c>sleep</c> keeps stdin open just long enough for that reply to come back. Finally it reports whether a
    /// <c>.mcp.json</c> sat in the CWD at all, so a caller can assert the handshake came from the ARGV rather than from
    /// the CLI's own project-scope discovery.
    /// </summary>
    internal static string ScriptBody =>
        "#!/bin/sh\n" +
        "cfg=''\n" +
        "next=0\n" +
        "for a in \"$@\"; do\n" +
        "  if [ \"$next\" = '1' ]; then cfg=\"$a\"; next=0; fi\n" +
        "  if [ \"$a\" = '--mcp-config' ]; then next=1; fi\n" +
        "done\n" +
        "summary='" + NoConfigSummary + "'\n" +
        "if [ -n \"$cfg\" ] && [ -f \"$cfg\" ]; then\n" +
        "  cmd=$(sed -n 's/.*\"command\": *\"\\(.*\\)\".*/\\1/p' \"$cfg\" | head -1)\n" +
        "  arg=$(grep -o '\"--[a-z-]*\"' \"$cfg\" | head -1 | tr -d '\"')\n" +
        "  sock=$(sed -n 's/.*\"" + McpSocketEnvVar + "\": *\"\\(.*\\)\".*/\\1/p' \"$cfg\" | head -1)\n" +
        "  tok=$(sed -n 's/.*\"" + McpTokenEnvVar + "\": *\"\\(.*\\)\".*/\\1/p' \"$cfg\" | head -1)\n" +
        "  reply=$({ printf '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}\\n'; sleep 3; } | " +
        "env " + McpSocketEnvVar + "=\"$sock\" " + McpTokenEnvVar + "=\"$tok\" \"$cmd\" \"$arg\" 2>/dev/null | head -n 1)\n" +
        "  case \"$reply\" in\n" +
        "    *protocolVersion*) summary='" + HandshakeSummary + "' ;;\n" +
        "    *) summary='" + NoReplySummary + "' ;;\n" +
        "  esac\n" +
        "fi\n" +
        "if [ -f './.mcp.json' ]; then summary=\"$summary " + CwdDeclarationPresent + "\"; else summary=\"$summary " + CwdDeclarationAbsent + "\"; fi\n" +
        "printf '{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"%s\"}]}}\\n' \"$summary\"\n" +
        "printf '{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"%s\",\"is_error\":false}\\n' \"$summary\"\n" +
        "exit 0\n";

    /// <summary>The keys the rendered declaration passes the run socket + token through — taken from the PRODUCTION writer, so a rename moves the fake with it instead of leaving it grepping for a key nothing writes.</summary>
    private const string McpSocketEnvVar = McpDeclarationWriter.SocketEnvVar;

    private const string McpTokenEnvVar = McpDeclarationWriter.TokenEnvVar;
}

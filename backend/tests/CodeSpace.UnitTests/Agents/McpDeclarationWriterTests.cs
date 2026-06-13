using System.Text.Json;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Mcp;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the exact on-disk shape of the MCP-server declaration a harness renders (via the writer) into its config-home so
/// a CLI spawns the <c>codespace-mcp</c> proxy with the run's socket + token: Claude Code's JSON <c>mcpServers</c> map and
/// Codex's TOML <c>[mcp_servers.&lt;name&gt;]</c> table. The contract that matters: the proxy command + a fixed
/// <c>--proxy</c> arg, and the socket/token carried in the server's ENV (never argv — the token is a capability). The two
/// renderers are pure (context in, string out), so these pin the rendered bytes; a CLI version recalibration changes the
/// shape here, visibly. Also the cross-binary drift guard: the Core-side env-var name literals MUST equal the proxy's own
/// <see cref="McpProxyEnv"/> constants (Rule 8 / Rule 12.5 mirror).
/// </summary>
[Trait("Category", "Unit")]
public sealed class McpDeclarationWriterTests
{
    private static McpDeclarationContext Context() => new()
    {
        ProxyCommand = "/abs/codespace-mcp",
        SocketPath = "/tmp/cs/run/mcp.sock",
        Token = "tok-ABC_123-xyz",
        ServerName = "codespace",
    };

    [Fact]
    public void Claude_json_declares_the_proxy_under_mcpServers_with_socket_and_token_in_env()
    {
        var json = McpDeclarationWriter.RenderClaudeJson(Context());

        using var doc = JsonDocument.Parse(json);   // valid JSON
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("codespace");

        server.GetProperty("command").GetString().ShouldBe("/abs/codespace-mcp");
        server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ShouldBe(new[] { "--proxy" });

        var env = server.GetProperty("env");
        env.GetProperty("CODESPACE_MCP_SOCKET").GetString().ShouldBe("/tmp/cs/run/mcp.sock");
        env.GetProperty("CODESPACE_RUN_TOKEN").GetString().ShouldBe("tok-ABC_123-xyz");
    }

    [Fact]
    public void Codex_toml_renders_the_exact_table_block_line_anchored()
    {
        var toml = McpDeclarationWriter.RenderCodexToml(Context());

        // No TOML parser is available, so assert the EXACT rendered block verbatim — the two tables + the precise keys —
        // rather than scattered substrings (which would pass even if the table structure were malformed).
        toml.ShouldBe(
            "[mcp_servers.codespace]\n" +
            "command = \"/abs/codespace-mcp\"\n" +
            "args = [\"--proxy\"]\n" +
            "env = { CODESPACE_MCP_SOCKET = \"/tmp/cs/run/mcp.sock\", CODESPACE_RUN_TOKEN = \"tok-ABC_123-xyz\" }\n");
    }

    [Fact]
    public void Toml_escapes_backslashes_and_quotes_so_a_pathological_value_cannot_break_the_table()
    {
        // A base64url token / a normal path needs no escaping, but a hostile value must not be able to inject a key or
        // close the string early. The TOML basic-string escaper handles backslash + double-quote.
        var toml = McpDeclarationWriter.RenderCodexToml(Context() with { Token = "a\"b\\c" });

        toml.ShouldContain("CODESPACE_RUN_TOKEN = \"a\\\"b\\\\c\"", customMessage: "the double-quote and backslash must be escaped, not literal");
    }

    [Fact]
    public void Both_formats_round_trip_a_real_base64url_token_unaltered()
    {
        // A minted token is base64url (A–Z a–z 0–9 - _) — none of the escaped chars — so it must survive byte-identical.
        var token = McpRunToken.Mint();

        var json = McpDeclarationWriter.RenderClaudeJson(Context() with { Token = token });
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("mcpServers").GetProperty("codespace").GetProperty("env").GetProperty("CODESPACE_RUN_TOKEN").GetString().ShouldBe(token);

        var toml = McpDeclarationWriter.RenderCodexToml(Context() with { Token = token });
        toml.ShouldContain($"CODESPACE_RUN_TOKEN = \"{token}\"", customMessage: "a base64url token contains no escaped chars, so it appears verbatim");
    }

    [Fact]
    public void Cross_binary_env_var_names_match_the_proxy_constants()
    {
        // The drift guard (Rule 8 / Rule 12.5): the Core-side env-var name literals the writer emits MUST equal the
        // proxy's OWN constants — the proxy reads socket+token from these env names. A rename on EITHER side silently
        // breaks the connect; assert EQUALITY (not literal-vs-literal) so the test fails the moment either drifts.
        McpDeclarationWriter.SocketEnvVar.ShouldBe(McpProxyEnv.SocketEnvVar);
        McpDeclarationWriter.TokenEnvVar.ShouldBe(McpProxyEnv.TokenEnvVar);
    }
}

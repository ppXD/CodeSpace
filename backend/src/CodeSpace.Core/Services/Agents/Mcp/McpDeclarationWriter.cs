using System.Text;
using System.Text.Json;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// The two literal MCP-declaration renderers a harness invokes to produce its config-home file CONTENT from a run's
/// <see cref="McpDeclarationContext"/>: Claude Code's <c>.mcp.json</c> (a JSON <c>mcpServers</c> map) or Codex's
/// <c>config.toml</c> (an <c>[mcp_servers.&lt;name&gt;]</c> table). Each names the proxy command + a fixed <c>--proxy</c>
/// arg and passes the run's socket + token through the server's <c>env</c> (NEVER argv — argv is world-readable via
/// <c>/proc</c>, the token is a capability). PURE — context in, string out, no IO — so the harness owns the format, the
/// runner writes the dumb bytes 0600, and a unit test pins the exact rendered bytes for each format.
///
/// <para>Anti-over-build (the MCP-transport design note): TWO literal writers invoked directly by their harness — no
/// generic serializer, no Format enum, no runner-side dispatch switch. The token is escaped per-format (JSON via the
/// serializer, TOML via a basic-string escaper) so a base64url token — which contains none of the escaped characters —
/// round-trips, and a future non-base64url token still can't break the file.</para>
/// </summary>
internal static class McpDeclarationWriter
{
    /// <summary>The env-var name the proxy reads the socket path from. Written into the declaration's server env; the <c>codespace-mcp</c> proxy reads the SAME name from its own env (<c>McpProxyEnv.SocketEnvVar</c>). A drift-pin test asserts they're equal (Rule 8).</summary>
    internal const string SocketEnvVar = "CODESPACE_MCP_SOCKET";

    /// <summary>The env-var name the proxy reads the run token from. Written into the declaration's server env; the <c>codespace-mcp</c> proxy reads the SAME name from its own env (<c>McpProxyEnv.TokenEnvVar</c>). A drift-pin test asserts they're equal (Rule 8).</summary>
    internal const string TokenEnvVar = "CODESPACE_RUN_TOKEN";

    /// <summary>The single fixed arg the harness passes the proxy command (a conventional verb; the proxy reads its real config from env). Kept here so the JSON + TOML writers agree.</summary>
    private static readonly string[] ProxyArgs = { "--proxy" };

    /// <summary>Claude Code's <c>.mcp.json</c>: a top-level <c>mcpServers</c> map keyed by the server name, each entry a {command, args, env}. The serializer escapes every string value (so the socket path + token are safe).</summary>
    internal static string RenderClaudeJson(McpDeclarationContext context)
    {
        var doc = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                [context.ServerName] = new Dictionary<string, object>
                {
                    ["command"] = context.ProxyCommand,
                    ["args"] = ProxyArgs,
                    ["env"] = new Dictionary<string, string>
                    {
                        [SocketEnvVar] = context.SocketPath,
                        [TokenEnvVar] = context.Token,
                    },
                },
            },
        };

        return JsonSerializer.Serialize(doc, ClaudeJsonOptions);
    }

    private static readonly JsonSerializerOptions ClaudeJsonOptions = new() { WriteIndented = true };

    /// <summary>Codex's <c>config.toml</c>: an <c>[mcp_servers.&lt;name&gt;]</c> table with a string command, an array of args, and an inline <c>env</c> table. String values use TOML basic-string quoting (escaped).</summary>
    internal static string RenderCodexToml(McpDeclarationContext context)
    {
        var sb = new StringBuilder();

        sb.Append("[mcp_servers.").Append(context.ServerName).Append("]\n");
        sb.Append("command = ").Append(TomlString(context.ProxyCommand)).Append('\n');
        sb.Append("args = [").Append(string.Join(", ", ProxyArgs.Select(TomlString))).Append("]\n");
        sb.Append("env = { ")
          .Append(SocketEnvVar).Append(" = ").Append(TomlString(context.SocketPath)).Append(", ")
          .Append(TokenEnvVar).Append(" = ").Append(TomlString(context.Token))
          .Append(" }\n");

        return sb.ToString();
    }

    /// <summary>Quote a value as a TOML basic string: wrap in double quotes and escape backslash, double-quote, and the control chars TOML requires. A base64url token / a filesystem path contains none of these, so this is a safety net, not a hot path.</summary>
    private static string TomlString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');

        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }
}

using System.Collections;

namespace CodeSpace.Mcp;

/// <summary>
/// Resolves the proxy's connect config (socket path + run token) from the environment. Pure — the runner stages the
/// socket path + token into the proxy's env before exec, so the verb token on argv (<c>mcp</c> / <c>--proxy</c>) is
/// ignored and config comes entirely from env. Fail-closed: a missing or empty value throws a usage error rather than
/// connecting anonymously. The env-var NAME constants are pinned by a test (Rule 8) — renaming one silently breaks the
/// runner side that sets it.
/// </summary>
internal static class McpProxyEnv
{
    /// <summary>The per-run capability token the proxy presents as the connection's first line. Set by the runner before exec; pinned (Rule 8).</summary>
    internal const string TokenEnvVar = "CODESPACE_RUN_TOKEN";

    /// <summary>The per-run UDS path the proxy connects to. Set by the runner before exec; pinned (Rule 8).</summary>
    internal const string SocketEnvVar = "CODESPACE_MCP_SOCKET";

    internal static (string SocketPath, string Token) ResolveConfig(string[] args, IDictionary env)
    {
        var socketPath = Read(env, SocketEnvVar);
        var token = Read(env, TokenEnvVar);

        if (string.IsNullOrEmpty(socketPath))
            throw new ArgumentException($"The MCP proxy requires a socket path in {SocketEnvVar}.");

        if (string.IsNullOrEmpty(token))
            throw new ArgumentException($"The MCP proxy requires a run token in {TokenEnvVar}.");

        return (socketPath, token);
    }

    private static string? Read(IDictionary env, string name) => env.Contains(name) ? env[name] as string : null;
}

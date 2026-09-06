namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// Where the <c>codespace-mcp</c> proxy's build output actually is. From the test bin
/// (<c>AppContext.BaseDirectory</c>, e.g. <c>.../tests/CodeSpace.IntegrationTests/bin/Debug/net10.0/</c>) we walk UP to
/// the directory holding <c>CodeSpace.sln</c>, then build
/// <c>&lt;root&gt;/src/CodeSpace.Mcp/bin/&lt;Configuration&gt;/net10.0/&lt;file&gt;</c>, deriving
/// <c>&lt;Configuration&gt;</c> from the test bin path. A build-only ProjectReference (csproj) guarantees the proxy is
/// produced before these tests run — but it is deliberately NOT copied into the test bin (it would pollute the
/// assembly scan), so every caller has to find it out here.
///
/// <para>One resolver, because two of them is how the pair drifts: a suite that hand-rolled its own Debug/Release walk
/// would keep resolving a path the other suite no longer means the moment the proxy's output layout moves.</para>
/// </summary>
internal static class BuiltMcpProxy
{
    /// <summary>The proxy's managed dll — what a <c>dotnet &lt;dll&gt; --proxy</c> child process is spawned from — or null when the build produced none (skip rather than fail: Rule 12.1 portability).</summary>
    internal static string? DllPathOrNull() => PathOrNull("codespace-mcp.dll");

    /// <summary>The proxy's apphost EXECUTABLE beside that dll — what an MCP declaration can name as its <c>command</c> — or null.</summary>
    internal static string? ExecutablePathOrNull() => PathOrNull("codespace-mcp");

    private static string? PathOrNull(string fileName)
    {
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug";

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CodeSpace.sln"))) dir = dir.Parent;
        if (dir is null) return null;

        var path = Path.Combine(dir.FullName, "src", "CodeSpace.Mcp", "bin", configuration, "net10.0", fileName);
        return File.Exists(path) ? path : null;
    }
}

using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins which resolvers an allowlist run's DNS is admitted to (<see cref="NamespaceResolvers"/>): the IPv4 nameservers
/// off loopback of the resolv.conf the run's namespace reads, and nothing a comment, an option or a malformed line
/// could add. Every address this returns is one port 53 is open to from the run, so a line read too generously is an
/// open door and one read too strictly is a resolver the run's tools query but cannot reach. The real kernel is the
/// sandbox lane's (<c>FilteredEgressNetnsE2ETests</c>: the run reaches port 53 at its resolver alone).
/// </summary>
[Trait("Category", "Unit")]
public sealed class NamespaceResolversTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cs-resolv-conf-").FullName;

    [Theory]
    [InlineData("nameserver 10.0.0.2\n", "10.0.0.2")]
    [InlineData("nameserver 10.0.0.2\nnameserver 192.168.65.7\n", "10.0.0.2,192.168.65.7")]                                  // in the file's order
    [InlineData("# written by the operator\nnameserver 10.0.0.2\n; a second comment\n", "10.0.0.2")]
    [InlineData("#nameserver 10.0.0.9\n# nameserver 10.0.0.8\n;nameserver 10.0.0.7\nnameserver 10.0.0.2\n", "10.0.0.2")]   // a commented-out nameserver names no resolver
    [InlineData("search corp.example\ndomain corp.example\noptions ndots:5 timeout:1 attempts:2\nnameserver 10.0.0.2\n", "10.0.0.2")]
    [InlineData("sortlist 10.0.0.7 10.0.0.6\noptions nameserver 10.0.0.9\nnameserver 10.0.0.2\n", "10.0.0.2")]             // an address on another keyword's line — sortlist's among them — is that line's value, not a resolver
    [InlineData("nameserver 10.0.0.2 # the primary\n", "10.0.0.2")]                                                         // trailing words after the address
    [InlineData("nameserver\t10.0.0.2\n", "10.0.0.2")]
    [InlineData("  nameserver 10.0.0.2\n", "10.0.0.2")]                                                                     // Go's resolver reads an indented line, though glibc skips it
    [InlineData("nameserver 127.0.0.53\noptions edns0 trust-ad\nsearch .\n", "")]                                          // systemd-resolved's stub: the namespace's own loopback
    [InlineData("nameserver 127.0.0.11\noptions ndots:0\n", "")]                                                            // Docker's embedded resolver, the same
    [InlineData("nameserver 127.1.2.3\nnameserver 10.0.0.2\n", "10.0.0.2")]                                                 // all of 127/8 is loopback
    [InlineData("nameserver ::1\nnameserver 2001:4860:4860::8888\nnameserver fe80::1%eth0\nnameserver 10.0.0.2\n", "10.0.0.2")]   // the namespace has no IPv6 route
    [InlineData("nameserver ::ffff:10.0.0.3\nnameserver ::ffff:127.0.0.53\nnameserver 10.0.0.2\nnameserver 10.0.0.3\n", "10.0.0.3,10.0.0.2")]   // an IPv4 address written as IPv6 is queried over IPv4: it is that address, in its place, once
    [InlineData("nameserver 10.0.0.2\nnameserver 10.0.0.2\nnameserver 10.0.0.3\nnameserver 10.0.0.2\n", "10.0.0.2,10.0.0.3")]   // each once
    [InlineData("nameserver 10.0.0.2\r\nnameserver 10.0.0.3\r\n", "10.0.0.2,10.0.0.3")]                                    // CRLF
    [InlineData("nameserver\nnameserver not-an-address\nnameserver 10.0.0.2#x\nnameservers 10.0.0.4\nnameserver10.0.0.5\n", "")]   // no address, not an address, not the keyword
    [InlineData("search corp.example\noptions ndots:1\n", "")]                                                              // no nameserver line
    [InlineData("", "")]
    public void Only_the_ipv4_nameservers_off_loopback_are_resolvers_the_run_can_reach(string resolvConf, string expected)
    {
        NamespaceResolvers.Parse(resolvConf).ShouldBe(expected.Length == 0 ? [] : expected.Split(','));
    }

    [Fact]
    public void A_resolv_conf_is_read_from_the_path_given_and_one_that_cannot_be_read_names_no_resolver()
    {
        // A namespace reading the same file finds no nameserver there either, and falls back to its own loopback, which
        // nothing answers: there is no resolver to admit, and the setup does not fail over it.
        var present = Path.Combine(_dir, "resolv.conf");
        File.WriteAllText(present, "search corp.example\nnameserver 10.0.0.2\n");

        NamespaceResolvers.Read(present).ShouldBe(["10.0.0.2"]);
        NamespaceResolvers.Read(Path.Combine(_dir, "absent")).ShouldBeEmpty();
        NamespaceResolvers.Read(_dir).ShouldBeEmpty("a directory where the file should be is unreadable too");
    }

    [Fact]
    public void The_resolv_conf_an_allowlist_namespace_reads_is_the_worker_s_own()
    {
        // `ip netns exec` binds /etc/netns/<ns>/* over /etc for what it runs, where that directory exists, and bubblewrap
        // binds /etc read-only inside it: with nothing creating one for the plan's namespace, the run reads the file the
        // worker reads. If anything ever creates one, the rules must be built from that file instead.
        NamespaceResolvers.ResolvConfPath.ShouldBe("/etc/resolv.conf");
        BubblewrapSandbox.ReadOnlyRootDirs.ShouldContain("/etc", "the confined CLI inside the namespace reads the same /etc");

        var plan = FilteredEgressPlan.Build("run-resolv01", ["1.1.1.1"], new EgressSubnetAllocator.Lease { Cidr = "198.19.70.16/30", HostIp = "198.19.70.17", NsIp = "198.19.70.18" }, ["10.0.0.2"]);
        var argv = plan.SetupCommands.Append(plan.ExecPrefix).Concat(plan.TeardownCommands).SelectMany(command => command);

        argv.ShouldNotContain(arg => arg.Contains("/etc", StringComparison.Ordinal) || arg.Contains("resolv", StringComparison.Ordinal), "the plan gives its namespace no resolv.conf of its own");
        plan.ExecPrefix.ShouldBe(["ip", "netns", "exec", plan.Namespace]);

        ProductionCodeNaming("etc/netns").ShouldBeEmpty("no production code may create a per-namespace /etc for the plan's namespace, which the rules would not be built from");
    }

    /// <summary>The non-comment lines of the production source that contain <paramref name="text"/>, as <c>file:line</c>.</summary>
    private static List<string> ProductionCodeNaming(string text)
    {
        var src = ProductionSourceRoot();

        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(file => File.ReadLines(file).Select((line, index) => (file, line, index)))
            .Where(entry => !entry.line.TrimStart().StartsWith("//", StringComparison.Ordinal) && entry.line.Contains(text, StringComparison.Ordinal))
            .Select(entry => $"{Path.GetRelativePath(src, entry.file)}:{entry.index + 1}")
            .ToList();
    }

    private static string ProductionSourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "backend", "src");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException($"'backend/src' was not found above '{AppContext.BaseDirectory}'. Run the unit suite from the repository checkout.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}

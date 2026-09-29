using System.Net;
using System.Net.Sockets;

namespace CodeSpace.Core.Services.Agents.Sandbox.Isolation;

/// <summary>
/// The resolvers an allowlist run's own tools query, and so the only addresses its DNS is admitted to
/// (<see cref="FilteredEgressPlan.Build"/>). Nothing gives a namespace the plan builds a resolv.conf of its own — no
/// <c>/etc/netns/&lt;ns&gt;/resolv.conf</c>, which <c>ip netns exec</c> would bind over the worker's — and bubblewrap binds
/// <c>/etc</c> read-only inside it, so the run reads the worker's <see cref="ResolvConfPath"/> and queries the
/// nameservers listed there. Only the IPv4 ones off loopback can answer it. A loopback nameserver (systemd-resolved's
/// 127.0.0.53, Docker's embedded 127.0.0.11) is the namespace's OWN loopback, where nothing listens, whatever the rules
/// say. The namespace has no IPv6 route, so an IPv6 nameserver is out of its reach too — save an IPv4 address written
/// as one (<c>::ffff:a.b.c.d</c>), which a resolver's socket sends over IPv4 and which is that IPv4 address here. A worker
/// whose resolv.conf lists no reachable resolver gives its allowlist runs no DNS at all: they could not have resolved a
/// name anyway.
///
/// <para>Read once, at setup, like the allowlist's own addresses: a resolv.conf the operator changes while a run is up
/// reaches that run's tools but not its rules.</para>
///
/// <para>A listed address the worker's own NAT rewrites still answers: the rules match what the run sent to, before
/// that NAT runs (<see cref="FilteredEgressPlan.Build"/>). An address rewritten at the socket, inside the run's own
/// namespace and before any packet exists, does not. Cilium's kube-proxy replacement does that with socket
/// load-balancing on, which reaches pod namespaces unless it is set to the host namespace only. The query then leaves
/// for a backend the file does not name, and is refused: an allowlist run there has no DNS.</para>
/// </summary>
public static class NamespaceResolvers
{
    /// <summary>The resolv.conf an allowlist namespace reads: the worker's own.</summary>
    public const string ResolvConfPath = "/etc/resolv.conf";

    private static readonly char[] LineWhitespace = [' ', '\t', '\r', '\f', '\v'];

    /// <summary>The resolvers in the resolv.conf at <paramref name="path"/> (<see cref="Parse"/>); none when it cannot be read, since a namespace reading the same file finds none there either.</summary>
    internal static IReadOnlyList<string> Read(string path)
    {
        try { return Parse(File.ReadAllText(path)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// The IPv4 nameservers off loopback in <paramref name="resolvConf"/>, in the file's order, each once. A line names a
    /// resolver only when its first word is <c>nameserver</c>, and the resolver is the word after it: a comment (a line
    /// starting <c>#</c> or <c>;</c>), <c>search</c>, <c>domain</c> and <c>options</c> lines, trailing words and a CRLF
    /// line end name nothing more. An indented line counts, as Go's resolver reads it though glibc skips it: the list
    /// may admit a resolver one of the run's tools skips, never refuse one another queries.
    /// </summary>
    internal static IReadOnlyList<string> Parse(string resolvConf) => resolvConf.Split('\n').Select(NameserverOn).OfType<IPAddress>().Select(AsSent).Where(IsReachableFromNamespace).Select(address => address.ToString()).Distinct().ToList();

    private static IPAddress? NameserverOn(string line) => line.Split(LineWhitespace, StringSplitOptions.RemoveEmptyEntries) is ["nameserver", var address, ..] && IPAddress.TryParse(address, out var parsed) ? parsed : null;

    /// <summary>An IPv4-mapped IPv6 address as the IPv4 address it is sent to; any other address as it is.</summary>
    private static IPAddress AsSent(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool IsReachableFromNamespace(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address);
}

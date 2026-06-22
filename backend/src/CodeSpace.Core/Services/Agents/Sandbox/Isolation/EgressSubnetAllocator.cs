using System.Security.Cryptography;
using System.Text;

namespace CodeSpace.Core.Services.Agents.Sandbox.Isolation;

/// <summary>
/// Hands out a COLLISION-FREE /30 subnet per active filtered-egress run (B3 stability hardening). nftables forward
/// chains are host-global and keyed on <c>ip saddr &lt;subnet&gt;</c>, so two CONCURRENTLY-active netns that shared a
/// /30 would co-evaluate each other's packets — a cross-run egress WIDEN (one run reaching the other's allowlist) or a
/// setup collision (the second run's <c>ip addr add</c> fails). A pure <c>SHA256(runId)</c>→/30 hash birthday-collides
/// (~2% at 50 concurrent runs in a 64k space), which fails the "very stable" bar. This process-wide allocator starts
/// from the hash-derived /30 and linear-probes a ~4.1M /30 space until it finds one NO OTHER active run on this host
/// holds, then releases it at teardown.
///
/// <para>Only the SUBNET is dynamically allocated — the netns / veth / nft-table NAMES stay runId-derived, and teardown
/// (which deletes by name and never references the subnet) is reconstructable from the runId alone. So this changes
/// nothing about the crash-resume teardown contract. Scope is THIS host's process: every launch flows through the
/// singleton runner, so the in-memory set covers all concurrent runs; an orphan netns from a CRASHED prior process is
/// reaped by the runId-derived reaper, not by this allocator.</para>
/// </summary>
public static class EgressSubnetAllocator
{
    /// <summary>A reserved /30 and its host (.1-of-the-block + 1) and netns (+2) addresses.</summary>
    public sealed record Lease
    {
        public required string Cidr { get; init; }     // 10.A.B.C/30
        public required string HostIp { get; init; }   // 10.A.B.(C+1)
        public required string NsIp { get; init; }      // 10.A.B.(C+2)
    }

    // 254 (octet2 ∈ 1..254) × 254 (octet3 ∈ 1..254) × 64 (octet4 block ∈ {0,4,…,252}) distinct /30s in 10.0.0.0/8.
    private const int Octet2Count = 254;
    private const int Octet3Count = 254;
    private const int Block4Count = 64;
    private const int Space = Octet2Count * Octet3Count * Block4Count;   // ≈ 4.13M

    private static readonly object _lock = new();
    private static readonly Dictionary<string, string> _byRun = new(StringComparer.Ordinal);   // runId -> Cidr
    private static readonly HashSet<string> _inUse = new(StringComparer.Ordinal);               // active Cidrs

    /// <summary>
    /// Reserve a /30 no OTHER active run holds. Deterministic-start (hash of <paramref name="runId"/>) + linear-probe.
    /// Idempotent: re-acquiring the same runId returns its existing lease, so a setup retry / re-entry is stable.
    /// </summary>
    public static Lease Acquire(string runId)
    {
        lock (_lock)
        {
            if (_byRun.TryGetValue(runId, out var held)) return LeaseFor(held);

            var start = StartIndex(runId);
            for (var probe = 0; probe < Space; probe++)
            {
                var cidr = CidrAt((start + probe) % Space);
                if (_inUse.Add(cidr))
                {
                    _byRun[runId] = cidr;
                    return LeaseFor(cidr);
                }
            }

            // Pathological: > 4.1M concurrent active runs on one host. Fail closed — abort rather than share a subnet.
            throw new InvalidOperationException("EgressSubnetAllocator: the /30 space is exhausted.");
        }
    }

    /// <summary>Free <paramref name="runId"/>'s reservation. A no-op when it never held one (e.g. teardown by a restarted worker that never acquired it) — safe + idempotent.</summary>
    public static void Release(string runId)
    {
        lock (_lock)
        {
            if (_byRun.Remove(runId, out var cidr)) _inUse.Remove(cidr);
        }
    }

    /// <summary>Test-only: the number of /30s currently reserved.</summary>
    internal static int ActiveCount { get { lock (_lock) { return _inUse.Count; } } }

    private static int StartIndex(string runId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(runId ?? ""));
        return (int)(BitConverter.ToUInt32(hash, 0) % (uint)Space);
    }

    private static string CidrAt(int index)
    {
        var block4 = index % Block4Count;                                 // 0..63  → octet4 = block4*4
        var octet3 = index / Block4Count % Octet3Count;                   // 0..253 → +1
        var octet2 = index / Block4Count / Octet3Count % Octet2Count;     // 0..253 → +1
        return $"10.{octet2 + 1}.{octet3 + 1}.{block4 * 4}/30";
    }

    private static Lease LeaseFor(string cidr)
    {
        var slash = cidr.IndexOf('/');
        var lastDot = cidr.LastIndexOf('.');
        var prefix = cidr[..lastDot];                                     // 10.A.B
        var baseOctet = int.Parse(cidr[(lastDot + 1)..slash]);            // C (the /30 block base)
        return new Lease { Cidr = cidr, HostIp = $"{prefix}.{baseOctet + 1}", NsIp = $"{prefix}.{baseOctet + 2}" };
    }
}

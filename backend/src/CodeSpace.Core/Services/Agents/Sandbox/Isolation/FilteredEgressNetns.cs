using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CodeSpace.Core.Services.Agents.Sandbox.Isolation;

/// <summary>
/// The privileged executor of a <see cref="FilteredEgressPlan"/> (B3.2 enforcement) — sets up a per-run filtered
/// network namespace, runs a command INSIDE it (so its only egress is the nftables allowlist), and tears the
/// namespace down. Needs <c>ip</c> + <c>nft</c> + <c>CAP_NET_ADMIN</c>/root, so it runs for real only in the
/// privileged sandbox-isolation CI job; <see cref="CanFilter"/> gates it everywhere else bubblewrap confines, and the
/// binaries alone where nothing does (<c>LocalProcessRunner.FiltersAllowlist</c>). Teardown is BEST-EFFORT
/// and ALWAYS runs (even on a setup failure mid-way), so a failed run never leaks a netns / veth / nft table.
/// </summary>
public static class FilteredEgressNetns
{

    /// <summary>How long a failed tools or filter probe stands before it is tried again (see <see cref="CapabilityProbe"/>): caching a transient failure for the process lifetime would misreport this worker's posture until it restarted.</summary>
    internal static readonly TimeSpan ProbeRetryInterval = TimeSpan.FromMinutes(1);

    /// <summary>The sysctl the plan's setup turns on (<c>sysctl -w net.ipv4.ip_forward=1</c>, <see cref="FilteredEgressPlan.Build"/>), as the file the filter probe reads.</summary>
    internal const string IpForwardPath = "/proc/sys/net/ipv4/ip_forward";

    private const string ToolsMissing = "ip or nft is not installed";

    private static readonly long ProcessStart = System.Diagnostics.Stopwatch.GetTimestamp();

    private static readonly CapabilityProbe Tools = new(() => ProbeSupported() ? null : ToolsMissing, () => System.Diagnostics.Stopwatch.GetElapsedTime(ProcessStart), ProbeRetryInterval);

    private static readonly CapabilityProbe Filter = new(() => FilterProbe(IpForwardPath), () => System.Diagnostics.Stopwatch.GetElapsedTime(ProcessStart), ProbeRetryInterval);

    /// <summary>True when <c>ip</c> + <c>nft</c> are present (the binaries the plan drives). Whether this process may USE them to filter a run is <see cref="CanFilter"/>, which a confining host's launch keys on; this alone gates what needs only the binaries — tearing a namespace down by name — and the allowlist plan of a host where bubblewrap does not confine, whose setup filters the run or aborts its launch (<c>LocalProcessRunner.FiltersAllowlist</c>). A failed probe is retried like the filter probe (<see cref="CapabilityProbe"/>): a fork that failed once at boot must not disable any of them for the process lifetime.</summary>
    public static bool IsSupported => Tools.Holds;

    /// <summary>
    /// True when this process has PROVED it can filter an allowlist run: the binaries are present, one throwaway
    /// namespace was created and deleted and nftables answered, and forwarding will be on once the setup has asked for
    /// it (<see cref="ForwardingProblem(string)"/>). The binaries alone are not enough — an image can ship them to a
    /// worker that runs without the privilege to use them, where an allowlist run planned Filtered would abort at its
    /// setup after its spend was admitted. So where bubblewrap confines, the launch's egress derivation keys on this
    /// (<c>LocalProcessRunner.FiltersAllowlist</c>): where it does not hold, an allowlist FAILS CLOSED to no egress
    /// (<see cref="SandboxEgressPolicy.Derive"/>), which bubblewrap enforces, and a brokered run still reaches its
    /// broker through the relay. Where nothing confines, nothing would enforce that, so the binaries alone still plan
    /// the run Filtered there. A proof is kept for the process; a failure is kept for
    /// <see cref="ProbeRetryInterval"/> and then probed again, and says why in <see cref="FilterUnavailableReason"/>.
    /// </summary>
    public static bool CanFilter => Filter.Holds;

    /// <summary>Why the last filter probe failed — the failed step and its output — or null when none has failed since the last proof. Read by the boot posture line, so the cause is not lost with the probe.</summary>
    public static string? FilterUnavailableReason => Filter.UnavailableReason;

    /// <summary>
    /// The filter probe <see cref="CanFilter"/> runs, with the forwarding file it asks about passed in
    /// (<see cref="IpForwardPath"/> there), so the root lane can ask the whole composition about a file root may not
    /// write, not only its parts. Its namespace step builds and deletes this process's one probe namespace for real, so
    /// it is asked beside <see cref="CanFilter"/> only once that has settled.
    /// </summary>
    internal static string? FilterProbe(string forwardingPath) => FilterProblem(IsSupported, ProbeNamespace, () => ForwardingProblem(forwardingPath));

    /// <summary>
    /// The first thing that stops this process filtering an allowlist run, in the order its setup meets them — the
    /// binaries, a namespace, forwarding — or null when nothing does. A step is asked only once the one before it
    /// holds, so a host without <c>ip</c> is never asked to run it. Pure over the three questions, so every row of the
    /// truth table is testable on a host that can answer none of them.
    /// </summary>
    internal static string? FilterProblem(bool toolsPresent, Func<string?> namespaceProblem, Func<string?> forwardingProblem)
    {
        if (!toolsPresent) return ToolsMissing;

        return namespaceProblem() ?? forwardingProblem();
    }

    /// <summary>
    /// Why forwarding would still be off after the setup's <c>sysctl -w net.ipv4.ip_forward=1</c>, or null when it
    /// will be on: the file already reads 1, or this process may write it. Asked with a read and <c>access(2)</c>,
    /// which change nothing. The setup's own write cannot be the question: procps' <c>sysctl -w</c> warns and exits 0
    /// on a read-only <c>/proc/sys</c> and on a denied write alike, so a setup that ran it would succeed with nothing
    /// forwarded — an allowlist whose allowed hosts are unreachable, recorded as filtered.
    /// </summary>
    internal static string? ForwardingProblem(string path) => ForwardingProblem(path, ReadForwarding(path), MayWrite(path));

    /// <summary><see cref="ForwardingProblem(string)"/> over what the file read (null when it could not be read) and whether this process may write it.</summary>
    internal static string? ForwardingProblem(string path, string? value, bool writable)
    {
        if (value == "1" || writable) return null;

        return value is null ? $"{path} could not be read, and this process may not write it" : $"{path} reads {value}, and this process may not write it";
    }

    private static string? ReadForwarding(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool MayWrite(string path) => !OperatingSystem.IsWindows() && Access(path, WriteOk) == 0;

    /// <summary><c>W_OK</c> from <c>unistd.h</c>, the same value on Linux and macOS.</summary>
    private const int WriteOk = 2;

    [DllImport("libc", EntryPoint = "access", SetLastError = true)]
    private static extern int Access(string path, int mode);

    /// <summary>The outcome of running a command inside the filtered netns: the command's exit code + its combined output, plus whether the netns setup itself succeeded.</summary>
    public sealed record Outcome
    {
        public required bool SetupOk { get; init; }
        public int ExitCode { get; init; }
        public string Output { get; init; } = "";
        public string? SetupError { get; init; }
    }

    /// <summary>The outcome of SETTING UP the filtered netns (without running anything in it) — for the DURABLE launch, which runs its detached process inside the netns via <see cref="ExecPrefix"/> and tears down separately at reap. On failure the partial setup is already cleaned up (fail-closed).</summary>
    public sealed record SetupResult
    {
        public required bool SetupOk { get; init; }

        /// <summary>The <c>ip netns exec &lt;ns&gt;</c> prefix a caller prepends to run its command inside the filtered netns. Empty when setup failed.</summary>
        public IReadOnlyList<string> ExecPrefix { get; init; } = Array.Empty<string>();

        public string? SetupError { get; init; }
    }

    /// <summary>
    /// Set up a fresh filtered netns whose only egress is <paramref name="allowedIps"/> (+ DNS to the resolvers the worker's
    /// resolv.conf names, which is the one the namespace reads — <see cref="NamespaceResolvers"/>), WITHOUT running anything
    /// in it — the durable launch then runs its detached process behind the returned <see cref="SetupResult.ExecPrefix"/>
    /// and calls <see cref="TeardownAsync"/> at reap. A setup failure is fail-closed: the partial netns is torn down
    /// immediately and SetupOk=false is returned. <paramref name="runId"/> seeds the unique (and teardown-reconstructable)
    /// netns/veth/table names.
    /// </summary>
    public static Task<SetupResult> SetupAsync(string runId, IReadOnlyList<string> allowedIps, int timeoutSeconds, CancellationToken cancellationToken) => SetupAsync(runId, allowedIps, NamespaceResolvers.ResolvConfPath, timeoutSeconds, cancellationToken);

    /// <summary><see cref="SetupAsync(string, IReadOnlyList{string}, int, CancellationToken)"/> with DNS admitted to the resolvers of the resolv.conf at <paramref name="resolvConfPath"/>, read here, at setup — so a real-kernel test can hand the rules the same file it points the namespace's view at.</summary>
    internal static async Task<SetupResult> SetupAsync(string runId, IReadOnlyList<string> allowedIps, string resolvConfPath, int timeoutSeconds, CancellationToken cancellationToken)
    {
        // Reserve a COLLISION-FREE /30 so no other run ON THIS HOST — this worker process or any other — shares a
        // subnet (a host-global nft-chain hazard). Released in TeardownAsync; the netns/table NAMES stay runId-derived
        // so teardown needs no setup-time state.
        if (await ReadHostRoutesAsync(timeoutSeconds, cancellationToken).ConfigureAwait(false) is not { } routes) return RoutesUnreadable;
        var (subnet, exhausted) = Reserve(EgressSubnetAllocator.Host, runId, routes);
        if (subnet is null) return new SetupResult { SetupOk = false, SetupError = exhausted };

        return await ApplyAsync(runId, FilteredEgressPlan.Build(runId, allowedIps, subnet, NamespaceResolvers.Read(resolvConfPath)), timeoutSeconds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reserve the run's /30, or say why the host has none free — every candidate routed or held. That outcome is a
    /// failed SETUP, reported like any other step's. A host whose reservation directory is unusable still throws its own
    /// typed refusal.
    /// </summary>
    internal static (EgressSubnetAllocator.Lease? Subnet, string? Exhausted) Reserve(EgressSubnetAllocator allocator, string runId, HostRoutedPrefixes routes)
    {
        try { return (allocator.Acquire(runId, routes), null); }
        catch (InvalidOperationException exhausted) { return (null, exhausted.Message); }
    }

    /// <summary>What the host already routes, so no /30 it uses is handed out (<see cref="HostRoutedPrefixes"/>); null when it cannot be read, which fails the setup closed rather than reserving blind.</summary>
    private static async Task<HostRoutedPrefixes?> ReadHostRoutesAsync(int timeoutSeconds, CancellationToken cancellationToken)
    {
        try
        {
            var (exit, output) = await RunHostAsync(HostRoutedPrefixes.ListArgv, stdin: null, timeoutSeconds, cancellationToken, stdoutOnly: true).ConfigureAwait(false);

            return exit == 0 ? HostRoutedPrefixes.Parse(output) : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { return null; }
    }

    private static readonly SetupResult RoutesUnreadable = new() { SetupOk = false, SetupError = $"{string.Join(' ', HostRoutedPrefixes.ListArgv)} could not be read, so no /30 could be chosen that this host does not already route" };

    /// <summary>Run a plan's setup, its route check and its ruleset, tearing down whatever was created the moment any step fails or throws. Internal so a real-kernel test can apply a plan on a /30 it owns.</summary>
    internal static async Task<SetupResult> ApplyAsync(string runId, FilteredEgressPlan plan, int timeoutSeconds, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var argv in plan.SetupCommands)
            {
                var (exit, output) = await RunHostAsync(argv, stdin: null, timeoutSeconds, cancellationToken).ConfigureAwait(false);
                if (exit != 0)
                {
                    await TeardownAsync(runId, CancellationToken.None).ConfigureAwait(false);   // fail-closed: clean up whatever the partial setup created
                    return new SetupResult { SetupOk = false, SetupError = $"{string.Join(' ', argv)} → exit {exit}: {Trim(output)}" };
                }
            }

            var (routeExit, routeOut) = await RunHostAsync(plan.RouteCheckArgv, stdin: null, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            if (plan.RouteCheckFailure(routeExit, routeOut) is { } misrouted)
            {
                await TeardownAsync(runId, CancellationToken.None).ConfigureAwait(false);
                return new SetupResult { SetupOk = false, SetupError = misrouted };
            }

            var (nftExit, nftOut) = await RunHostAsync(plan.NftApplyArgv, stdin: plan.NftRuleset, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            if (nftExit != 0)
            {
                await TeardownAsync(runId, CancellationToken.None).ConfigureAwait(false);
                return new SetupResult { SetupOk = false, SetupError = $"nft -f - → exit {nftExit}: {Trim(nftOut)}" };
            }

            return new SetupResult { SetupOk = true, ExecPrefix = plan.ExecPrefix };
        }
        catch (Exception ex)
        {
            // ANY throw mid-setup (a missing ip/nft binary → process.Start throws; a broken nft pipe → WriteAsync
            // throws) must ALSO fail CLOSED — tear down whatever was created, never leak a half-built netns. (The
            // explicit exit!=0 branches already returned, so this only fires for a genuine throw — no double-teardown.)
            await TeardownAsync(runId, CancellationToken.None).ConfigureAwait(false);
            return new SetupResult { SetupOk = false, SetupError = $"setup threw: {ex.Message}" };
        }
    }

    /// <summary>
    /// Tear down the filtered netns for <paramref name="runId"/> — best-effort, reconstructed PURELY from the runId
    /// (the netns/veth/table names are runId-derived), so it works even when called by a DIFFERENT worker after a
    /// crash/resume, or by the spool reaper from a persisted handle, with no setup-time state. Idempotent: deleting an
    /// already-gone ns/table is a no-op the best-effort wrapper swallows.
    /// </summary>
    public static async Task TeardownAsync(string runId, CancellationToken cancellationToken)
    {
        // Free the run's reserved /30 (no-op if this process never held it — e.g. a reaper on a restarted worker;
        // the crashed worker's own handles already dropped its locks).
        EgressSubnetAllocator.Host.Release(runId);

        // The teardown argv are reconstructed PURELY from the runId (names are runId-derived) — no setup-time subnet.
        foreach (var argv in FilteredEgressPlan.TeardownCommandsFor(runId))
            try { await RunHostAsync(argv, stdin: null, timeoutSeconds: 15, CancellationToken.None).ConfigureAwait(false); }
            catch { /* best-effort cleanup — never let teardown throw */ }
    }

    /// <summary>
    /// Run <paramref name="command"/> inside a fresh filtered netns whose only egress is <paramref name="allowedIps"/>
    /// (+ DNS to its resolvers). Sets up, runs, and ALWAYS tears down — the SYNCHRONOUS path the B3.2a CI E2E drives. The
    /// durable launch uses <see cref="SetupAsync(string, IReadOnlyList{string}, int, CancellationToken)"/> + <see cref="TeardownAsync"/> directly instead.
    /// </summary>
    public static async Task<Outcome> RunAsync(string runId, IReadOnlyList<string> allowedIps, string command, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var setup = await SetupAsync(runId, allowedIps, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        if (!setup.SetupOk) return new Outcome { SetupOk = false, SetupError = setup.SetupError };

        try
        {
            var execArgv = setup.ExecPrefix.Concat(new[] { command }).Concat(args).ToList();
            var (cmdExit, cmdOut) = await RunHostAsync(execArgv, stdin: null, timeoutSeconds, cancellationToken).ConfigureAwait(false);

            return new Outcome { SetupOk = true, ExitCode = cmdExit, Output = cmdOut };
        }
        finally
        {
            await TeardownAsync(runId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Build and delete one throwaway namespace and ask nftables to answer: null when both work, else the step that did
    /// not. The name is this PROCESS's own, deleted first and always — whether or not the add reported success, since
    /// an add killed at its timeout may still have created it — so a probe can leave at most one namespace behind per
    /// worker, and the next probe from the same process removes it.
    /// </summary>
    private static string? ProbeNamespace()
    {
        var probe = $"cs-filter-probe-{Environment.ProcessId}";

        try
        {
            RunHostAsync(new[] { "ip", "netns", "del", probe }, null, 10, CancellationToken.None).GetAwaiter().GetResult();

            var (addExit, addOutput) = RunHostAsync(new[] { "ip", "netns", "add", probe }, null, 10, CancellationToken.None).GetAwaiter().GetResult();
            if (addExit != 0) return $"ip netns add → exit {addExit}: {Trim(addOutput)}";

            var (nftExit, nftOutput) = RunHostAsync(new[] { "nft", "list", "tables" }, null, 10, CancellationToken.None).GetAwaiter().GetResult();
            return nftExit == 0 ? null : $"nft list tables → exit {nftExit}: {Trim(nftOutput)}";
        }
        catch (Exception exception) { return $"the probe threw: {exception.Message}"; }
        finally
        {
            try { RunHostAsync(new[] { "ip", "netns", "del", probe }, null, 10, CancellationToken.None).GetAwaiter().GetResult(); } catch { /* best-effort: the next probe deletes it first */ }
        }
    }

    private static bool ProbeSupported()
    {
        try
        {
            return RunHostAsync(new[] { "ip", "-Version" }, null, 10, CancellationToken.None).GetAwaiter().GetResult().Exit == 0
                && RunHostAsync(new[] { "nft", "--version" }, null, 10, CancellationToken.None).GetAwaiter().GetResult().Exit == 0;
        }
        catch { return false; }
    }

    private static async Task<(int Exit, string Output)> RunHostAsync(IReadOnlyList<string> argv, string? stdin, int timeoutSeconds, CancellationToken cancellationToken, bool stdoutOnly = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = argv[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
        };
        foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null && !stdoutOnly) lock (output) output.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ } return (124, output.ToString() + "\n[timed out]"); }

        return (process.ExitCode, output.ToString());
    }

    private static string Trim(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim() + "…";
}

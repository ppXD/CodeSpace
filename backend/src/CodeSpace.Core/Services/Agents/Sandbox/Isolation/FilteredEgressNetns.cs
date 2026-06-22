using System.Diagnostics;
using System.Text;

namespace CodeSpace.Core.Services.Agents.Sandbox.Isolation;

/// <summary>
/// The privileged executor of a <see cref="FilteredEgressPlan"/> (B3.2 enforcement) — sets up a per-run filtered
/// network namespace, runs a command INSIDE it (so its only egress is the nftables allowlist), and tears the
/// namespace down. Needs <c>ip</c> + <c>nft</c> + <c>CAP_NET_ADMIN</c>/root, so it runs for real only in the
/// privileged sandbox-isolation CI job; <see cref="IsSupported"/> gates it everywhere else. Teardown is BEST-EFFORT
/// and ALWAYS runs (even on a setup failure mid-way), so a failed run never leaks a netns / veth / nft table.
/// </summary>
public static class FilteredEgressNetns
{
    private static readonly Lazy<bool> _supported = new(ProbeSupported);

    /// <summary>True when <c>ip</c> + <c>nft</c> are present (the binaries the plan drives). Actual privilege to create a netns is exercised at run time — a setup failure fails closed.</summary>
    public static bool IsSupported => _supported.Value;

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
    /// Set up a fresh filtered netns whose only egress is <paramref name="allowedIps"/> (+ DNS), WITHOUT running anything
    /// in it — the durable launch then runs its detached process behind the returned <see cref="SetupResult.ExecPrefix"/>
    /// and calls <see cref="TeardownAsync"/> at reap. A setup failure is fail-closed: the partial netns is torn down
    /// immediately and SetupOk=false is returned. <paramref name="runId"/> seeds the unique (and teardown-reconstructable)
    /// netns/veth/table names.
    /// </summary>
    public static async Task<SetupResult> SetupAsync(string runId, IReadOnlyList<string> allowedIps, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var plan = FilteredEgressPlan.Build(runId, allowedIps);

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
    /// crash/resume, or as an orphan sweep, with no setup-time state. Idempotent: deleting an already-gone ns/table is
    /// a no-op the best-effort wrapper swallows.
    /// </summary>
    public static async Task TeardownAsync(string runId, CancellationToken cancellationToken)
    {
        // The IPs are irrelevant to teardown (it deletes by name) — pass an empty allowlist to rebuild the same names.
        var plan = FilteredEgressPlan.Build(runId, Array.Empty<string>());

        foreach (var argv in plan.TeardownCommands)
            try { await RunHostAsync(argv, stdin: null, timeoutSeconds: 15, CancellationToken.None).ConfigureAwait(false); }
            catch { /* best-effort cleanup — never let teardown throw */ }
    }

    /// <summary>
    /// Run <paramref name="command"/> inside a fresh filtered netns whose only egress is <paramref name="allowedIps"/>
    /// (+ DNS). Sets up, runs, and ALWAYS tears down — the SYNCHRONOUS path the B3.2a CI E2E drives. The durable launch
    /// uses <see cref="SetupAsync"/> + <see cref="TeardownAsync"/> directly instead.
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

    private static bool ProbeSupported()
    {
        try
        {
            return RunHostAsync(new[] { "ip", "-Version" }, null, 10, CancellationToken.None).GetAwaiter().GetResult().Exit == 0
                && RunHostAsync(new[] { "nft", "--version" }, null, 10, CancellationToken.None).GetAwaiter().GetResult().Exit == 0;
        }
        catch { return false; }
    }

    private static async Task<(int Exit, string Output)> RunHostAsync(IReadOnlyList<string> argv, string? stdin, int timeoutSeconds, CancellationToken cancellationToken)
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
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

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

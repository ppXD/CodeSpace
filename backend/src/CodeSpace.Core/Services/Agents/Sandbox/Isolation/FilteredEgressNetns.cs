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

    /// <summary>
    /// Run <paramref name="command"/> inside a fresh filtered netns whose only egress is <paramref name="allowedIps"/>
    /// (+ DNS). Sets up, runs, and ALWAYS tears down. A setup failure fails closed (SetupOk=false, the command is not
    /// run). <paramref name="runId"/> seeds the unique netns/veth/table names.
    /// </summary>
    public static async Task<Outcome> RunAsync(string runId, IReadOnlyList<string> allowedIps, string command, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var plan = FilteredEgressPlan.Build(runId, allowedIps);

        try
        {
            foreach (var argv in plan.SetupCommands)
            {
                var (exit, output) = await RunHostAsync(argv, stdin: null, timeoutSeconds, cancellationToken).ConfigureAwait(false);
                if (exit != 0) return new Outcome { SetupOk = false, SetupError = $"{string.Join(' ', argv)} → exit {exit}: {Trim(output)}" };
            }

            var (nftExit, nftOut) = await RunHostAsync(plan.NftApplyArgv, stdin: plan.NftRuleset, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            if (nftExit != 0) return new Outcome { SetupOk = false, SetupError = $"nft -f - → exit {nftExit}: {Trim(nftOut)}" };

            var execArgv = plan.ExecPrefix.Concat(new[] { command }).Concat(args).ToList();
            var (cmdExit, cmdOut) = await RunHostAsync(execArgv, stdin: null, timeoutSeconds, cancellationToken).ConfigureAwait(false);

            return new Outcome { SetupOk = true, ExitCode = cmdExit, Output = cmdOut };
        }
        finally
        {
            foreach (var argv in plan.TeardownCommands)
                try { await RunHostAsync(argv, stdin: null, timeoutSeconds: 15, CancellationToken.None).ConfigureAwait(false); }
                catch { /* best-effort cleanup — never let teardown throw */ }
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

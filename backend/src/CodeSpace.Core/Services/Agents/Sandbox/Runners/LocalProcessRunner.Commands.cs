using System.Collections.ObjectModel;
using System.Diagnostics;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Sandbox.Runners;

public sealed partial class LocalProcessRunner
{
    private sealed record CommandIsolationContext(SandboxSpec Spec, string? ConfigHome, string? McpDeclarationPath, IReadOnlyList<string> EgressPrefix, IReadOnlyList<string> CgroupPrefix);

    private async Task<CommandInvocation> PrepareCommandAsync(SandboxSpec spec, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BubblewrapSandbox.EnsureSatisfiable(BubblewrapSandbox.Available, BubblewrapSandbox.IsRequired);

        var invocation = new CommandInvocation(BuildStartInfo(spec), _logger);
        try
        {
            var key = Guid.NewGuid().ToString("N");
            var cgroup = await SetupCgroupAsync(spec, key, cancellationToken).ConfigureAwait(false);
            invocation.CgroupKey = cgroup.Key;
            var egress = await SetupEgressNetnsAsync(spec, key, cancellationToken).ConfigureAwait(false);
            invocation.EgressKey = egress.Key;

            // Re-layer the spec env with the broker host resolved (the start info was built before the run's /30
            // existed). A no-op for every run whose env does not mention the token — the values are identical.
            foreach (var (name, value) in ResolveModelBrokerHost(spec, egress.GatewayIp).Environment) invocation.StartInfo.Environment[name] = value;

            if (spec.ConfigHomeEnvVars.Count > 0)
            {
                invocation.ConfigHome = Path.Combine(Path.GetTempPath(), "codespace-command-" + key);
                Directory.CreateDirectory(invocation.ConfigHome);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(invocation.ConfigHome, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                foreach (var name in spec.ConfigHomeEnvVars) invocation.StartInfo.Environment[name] = invocation.ConfigHome;
            }

            var declaration = WriteMcpDeclaration(spec, invocation.ConfigHome);
            WriteConfigHomeFiles(spec.ConfigHomeFiles, invocation.ConfigHome);
            var argv = new Collection<string>();
            AppendChildCommand(argv, new CommandIsolationContext(spec, invocation.ConfigHome, declaration, egress.ExecPrefix, cgroup.ExecPrefix));
            invocation.StartInfo.FileName = argv[0];
            invocation.StartInfo.ArgumentList.Clear();
            foreach (var arg in argv.Skip(1)) invocation.StartInfo.ArgumentList.Add(arg);
            cancellationToken.ThrowIfCancellationRequested();
            return invocation;
        }
        catch
        {
            await invocation.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class CommandPipeLifetime(Process process, ILogger logger) : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        public CancellationToken Token => _cancellation.Token;

        public void Dispose()
        {
            _cancellation.Cancel();
            try { process.StandardOutput.Dispose(); }
            catch (IOException error) { logger.LogWarning(error, "Command stdout pipe close failed"); }
            try { process.StandardError.Dispose(); }
            catch (IOException error) { logger.LogWarning(error, "Command stderr pipe close failed"); }
            _cancellation.Dispose();
        }
    }

    /// <summary>Owns short-lived invocation resources; observation keeps the existing batch/stream semantics.</summary>
    private sealed class CommandInvocation(ProcessStartInfo startInfo, ILogger logger) : IAsyncDisposable
    {
        public ProcessStartInfo StartInfo { get; } = startInfo;
        public string? ConfigHome { get; set; }
        public string? EgressKey { get; set; }
        public string? CgroupKey { get; set; }
        private string? CgroupRoot { get; } = CgroupResourceLimit.CgroupRoot;

        public SandboxStatus ExitStatus(int exitCode) => exitCode == 0 ? SandboxStatus.Success : CgroupRoot is { } root && CgroupKey is { } key && CgroupResourceLimit.OomKillCount(root, key) > 0 ? SandboxStatus.ResourceExhausted : SandboxStatus.Failed;

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (EgressKey is { } egressKey) await FilteredEgressNetns.TeardownAsync(egressKey, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) { logger.LogWarning(error, "Command network cleanup failed for {EgressKey}", EgressKey); }
            try
            {
                if (CgroupRoot is { } root && CgroupKey is { } cgroupKey) await CgroupResourceLimit.TeardownAsync(root, cgroupKey, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) { logger.LogWarning(error, "Command cgroup cleanup failed for {CgroupKey}", CgroupKey); }
            if (ConfigHome is { } home)
            {
                try { Directory.Delete(home, recursive: true); }
                catch (DirectoryNotFoundException) { }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(error, "Command configuration cleanup failed for {ConfigHome}; execution outcome is preserved", home);
                }
            }
        }
    }
}

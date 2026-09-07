using System.Diagnostics;
using System.Text.Json;
using CodeSpace.Core.Services.Agents.Sandbox.Exceptions;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Messages.Agents;
using CodeSpace.NativeLaunch;

namespace CodeSpace.Core.Services.Agents.Sandbox.Runners;

public sealed partial class LocalProcessRunner
{
    public const string RunnerHostPathEnvVar = "CODESPACE_RUNNER_HOST_PATH";

    /// <summary>The compatibility producer binds its existing spool key. A native execution/attempt producer can supply the typed identity without coupling it to a worker lease.</summary>
    public Task<SandboxHandle> LaunchAsync(SandboxSpec spec, string spoolKey, CancellationToken cancellationToken) => LaunchOrDiscoverAsync(new SandboxLaunchRequest(spec, spoolKey), cancellationToken);

    public async Task<SandboxHandle> LaunchOrDiscoverAsync(SandboxLaunchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var spec = NativeLaunchProtocol.Freeze(request.Spec); // Snapshot before the first await: caller-owned lists cannot mutate this launch.
        ValidateLaunchKey(request);
        var hash = NativeLaunchProtocol.SpecHash(spec);
        var spool = SpoolDirectoryFor(request.SpoolKey);
        var directory = NativeLaunchFiles.DirectoryFor(spool);
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var record = await BindLaunchAsync(request, hash, directory, cancellationToken).ConfigureAwait(false);

        // File existence is only a scheduling optimization. The independent bootstrap itself owns the create-only
        // commitment, so two OS observers seeing "absent" cannot release two executions.
        if (!File.Exists(NativeLaunchFiles.PathFor(directory, NativeLaunchProtocol.CommitmentFile)))
            await StartBrokerAsync(new BrokerStart(request.SpoolKey, spec, spool, directory), cancellationToken).ConfigureAwait(false);

        return await DiscoverHandleAsync(record, directory, spool, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<NativeLaunchRecord> BindLaunchAsync(SandboxLaunchRequest request, string hash, string directory, CancellationToken cancellationToken)
    {
        var spool = Path.GetDirectoryName(directory)!;
        if (!File.Exists(NativeLaunchFiles.PathFor(directory, NativeLaunchProtocol.RequestFile)) && new[] { PidFile, ExitMarkerFile, StdoutFile, StderrFile }.Any(file => Path.Exists(Path.Combine(spool, file))))
            throw new NativeLaunchException("legacy-slot", "An existing spool without a native launch request cannot authorize a new execution.");
        var now = DateTimeOffset.UtcNow;
        var candidate = new NativeLaunchRecord
        {
            SpecHash = hash, SpoolKey = request.SpoolKey, Identity = request.Identity, Host = CurrentHost, BootId = NativeProcess.BootId, CreatedAt = now,
            Deadline = request.Spec.TimeoutSeconds is > 0 and { } seconds ? now.AddSeconds(seconds) : DateTimeOffset.MaxValue,
        };
        NativeLaunchFiles.TryCreate(directory, NativeLaunchProtocol.RequestFile, candidate);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var recorded = NativeLaunchFiles.Read<NativeLaunchRecord>(directory, NativeLaunchProtocol.RequestFile);
                if (recorded.Version != NativeLaunchProtocol.Version || recorded.SpecHash != hash || recorded.SpoolKey != request.SpoolKey || recorded.Identity != request.Identity)
                    throw new NativeLaunchException("binding-conflict", "The launch slot is bound to a different immutable invocation or identity.");
                if (recorded.BootId != NativeProcess.BootId || !string.Equals(recorded.Host, CurrentHost, StringComparison.OrdinalIgnoreCase))
                    throw new NativeLaunchException("foreign-host", "This launch belongs to another host or boot; its PID cannot be adopted here.");
                return recorded;
            }
            catch (JsonException) when (watch.Elapsed < TimeSpan.FromSeconds(2)) { await Task.Delay(10, cancellationToken).ConfigureAwait(false); }
        }
    }

    private async Task StartBrokerAsync(BrokerStart request, CancellationToken cancellationToken)
    {
        var binary = RunnerHostBinaryPath();
        var info = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("broker"); info.ArgumentList.Add(request.Directory);
        using var process = new Process { StartInfo = info };
        await ProcessLaunchThread.StartAsync(process, cancellationToken).ConfigureAwait(false);
        var transmissionStarted = false;
        string? cgroupKey = null;
        string? egressKey = null;
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshake.CancelAfter(TimeSpan.FromSeconds(5));
            var response = await process.StandardOutput.ReadLineAsync(handshake.Token).ConfigureAwait(false);
            if (response == "discover") return;
            if (response != "owned") throw new NativeLaunchException("bootstrap-unavailable", "The native bootstrap did not acknowledge its durable start commitment.");

            var cgroup = await SetupCgroupAsync(request.Spec, request.SpoolKey, cancellationToken).ConfigureAwait(false);
            cgroupKey = cgroup.Key;
            var egress = await SetupEgressNetnsAsync(request.Spec, request.SpoolKey, cancellationToken).ConfigureAwait(false);
            egressKey = egress.Key;
            var command = BuildDurableStartInfo(request.Spec, request.Spool, egress.ExecPrefix, cgroup.ExecPrefix, bootstrapSession: true);
            var invocation = new NativeLaunchInvocation
            {
                Spec = request.Spec, ReadOnlyPaths = request.Spec.ReadOnlyPaths, CaptureBudget = request.Spec.CaptureBudget,
                Command = command.FileName, Args = command.ArgumentList.ToArray(), WorkingDirectory = command.WorkingDirectory,
                Environment = command.Environment.ToDictionary(pair => pair.Key, pair => pair.Value), EgressNetnsKey = egressKey, CgroupRunKey = cgroupKey,
                Confinement = BubblewrapSandbox.DeriveConfinement(BubblewrapSandbox.Available, BubblewrapSandbox.UnavailableReason, ShareNetwork(request.Spec, egress.ExecPrefix), EgressAllowlist(request.Spec, egress.ExecPrefix)),
            };
            cancellationToken.ThrowIfCancellationRequested();
            transmissionStarted = true; // A write/flush exception may be an ACK loss. Never tear down an execution on that assumption.
            await NativeLaunchFiles.WriteFrameAsync(process.StandardInput.BaseStream, invocation, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!transmissionStarted)
            {
                if (egressKey is { Length: > 0 }) await FilteredEgressNetns.TeardownAsync(egressKey, CancellationToken.None).ConfigureAwait(false);
                if (cgroupKey is { Length: > 0 } && CgroupResourceLimit.CgroupRoot is { } root) await CgroupResourceLimit.TeardownAsync(root, cgroupKey, CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        finally { process.StandardInput.Close(); }
    }

    private static async Task<SandboxHandle> DiscoverHandleAsync(NativeLaunchRecord request, string directory, string spool, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NativeLaunchReceipt? receipt = null;
            try { receipt = NativeLaunchFiles.Read<NativeLaunchReceipt>(directory, NativeLaunchProtocol.ReceiptFile); }
            catch (FileNotFoundException) { }
            catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                throw new NativeLaunchException("indeterminate", "The committed launch receipt is unreadable; automatic re-execution is forbidden.");
            }
            if (receipt is not null)
            {
                if (receipt.Broker is null || string.IsNullOrWhiteSpace(receipt.State)) throw new NativeLaunchException("indeterminate", "The committed launch receipt is incomplete; automatic re-execution is forbidden.");
                if (receipt.SpecHash != request.SpecHash || receipt.Broker.BootId != request.BootId) throw new NativeLaunchException("receipt-conflict", "The launch receipt does not match its immutable request.");
                if (receipt.State is "ready" or "exited" or "stopped" && receipt.Execution is { } execution)
                    return new SandboxHandle { Kind = LocalKind, ProcessId = execution.ProcessId, ProcessStartTimeUtc = new DateTimeOffset(execution.StartTimeUtcTicks, TimeSpan.Zero), LaunchHost = request.Host, SpoolDirectory = spool, Deadline = request.Deadline, Confinement = receipt.Confinement, CgroupRunKey = receipt.CgroupRunKey, EgressNetnsKey = receipt.EgressNetnsKey };
                if (receipt.State is "rejected" or "indeterminate" || !NativeProcess.IsAlive(receipt.Broker))
                    throw new NativeLaunchException("indeterminate", $"The start commitment was consumed without a confirmed execution receipt ({receipt.Problem ?? receipt.State}); automatic re-execution is forbidden.");
            }
            if (watch.Elapsed >= TimeSpan.FromSeconds(35)) throw new NativeLaunchException("indeterminate", "The launch receipt is unavailable; automatic re-execution is forbidden.");
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static string RunnerHostBinaryPath()
    {
        var path = Environment.GetEnvironmentVariable(RunnerHostPathEnvVar) ?? Path.Combine(AppContext.BaseDirectory, "runner-host", NativeLaunchProtocol.BinaryName);
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new NativeLaunchException("bootstrap-missing", "The bundled native runner host is unavailable; durable launch cannot safely fall back.");
        return path;
    }

    private static void ValidateLaunchKey(SandboxLaunchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SpoolKey) || request.SpoolKey is "." or ".." || request.SpoolKey.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '\0']) >= 0)
            throw new ArgumentException("Launch slot must be one local path segment.", nameof(request));
        if (request.Identity is { } identity && new[] { identity.TeamId, identity.AgentRunId, identity.ExecutionId, identity.AttemptId }.Any(value => value == Guid.Empty))
            throw new ArgumentException("An explicit native launch identity must be complete.", nameof(request));
    }

    private static bool NativeDeadlineExpired(SandboxHandle handle)
    {
        try { return NativeLaunchFiles.Read<NativeLaunchStop>(NativeLaunchFiles.DirectoryFor(handle.SpoolDirectory), NativeLaunchProtocol.StopFile).Reason == "deadline"; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private sealed record BrokerStart(string SpoolKey, SandboxSpec Spec, string Spool, string Directory);
}

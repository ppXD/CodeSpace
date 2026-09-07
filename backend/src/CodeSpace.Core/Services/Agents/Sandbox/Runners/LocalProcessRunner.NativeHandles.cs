using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using CodeSpace.Messages.Agents;
using CodeSpace.NativeLaunch;

namespace CodeSpace.Core.Services.Agents.Sandbox.Runners;

public sealed partial class LocalProcessRunner
{
    // True + null is the explicit compatibility path: no reference and no visible registry. This cannot
    // distinguish pre-native handles from A-era handles whose registry was lost. A present reference never
    // takes that path; an A-era handle with a retained registry is validated without inventing a birth key.
    private static bool TryResolveNativeHandle(SandboxHandle handle, out NativeHandleBinding? binding)
    {
        binding = null;
        try
        {
            var directory = NativeLaunchFiles.DirectoryFor(handle.SpoolDirectory);
            if (handle.NativeLaunch is null && !Path.Exists(directory)) return true;
            var request = NativeLaunchFiles.Read<NativeLaunchRecord>(directory, NativeLaunchProtocol.RequestFile);
            var receipt = NativeLaunchFiles.Read<NativeLaunchReceipt>(directory, NativeLaunchProtocol.ReceiptFile);
            var commitment = NativeLaunchFiles.Read<NativeLaunchCommitment>(directory, NativeLaunchProtocol.CommitmentFile);
            var execution = NativeLaunchFiles.Read<NativeProcessIdentity>(directory, NativeLaunchProtocol.ExecutionFile);
            if (request.Version != NativeLaunchProtocol.Version || string.IsNullOrWhiteSpace(request.Host) || string.IsNullOrWhiteSpace(request.SpecHash) || request.SpecHash != receipt.SpecHash || request.SpecHash != commitment.SpecHash) return false;
            if (receipt.State is not ("ready" or "exited" or "stopped") || receipt.Broker is null || receipt.Execution is null || receipt.Broker != commitment.Broker || receipt.Execution != execution) return false;
            if (string.IsNullOrWhiteSpace(execution.StartKey) || string.IsNullOrWhiteSpace(execution.BootId) || execution.BootId != request.BootId || receipt.Broker.BootId != request.BootId) return false;
            if (handle.Kind != LocalKind || handle.ProcessId != execution.ProcessId || handle.ProcessStartTimeUtc?.UtcTicks != execution.StartTimeUtcTicks) return false;
            if (!string.Equals(handle.LaunchHost, request.Host, StringComparison.OrdinalIgnoreCase) || handle.Deadline != request.Deadline || Path.GetFileName(handle.SpoolDirectory) != request.SpoolKey) return false;
            if (handle.CgroupRunKey != receipt.CgroupRunKey || handle.EgressNetnsKey != receipt.EgressNetnsKey) return false;
            if (handle.NativeLaunch is { } reference && (reference.Version != request.Version || reference.SpecHash != request.SpecHash || reference.Identity != request.Identity || reference.Execution != execution)) return false;
            binding = new NativeHandleBinding(request, receipt);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException) { return false; }
    }

    private static SandboxProbe ProbeNativeHandle(SandboxHandle handle, NativeHandleBinding binding)
    {
        // A bound terminal marker remains readable on shared storage. PID-derived answers require this boot.
        if (TryReadExitCode(Path.Combine(handle.SpoolDirectory, ExitMarkerFile), out var exitCode)) return new SandboxProbe { State = SandboxRunState.Exited, ExitCode = exitCode };
        if (!NativeHandleIsLocal(binding)) return new SandboxProbe { State = SandboxRunState.Indeterminate };
        return new SandboxProbe { State = ObserveNativeProcess(binding.Receipt.Execution!) };
    }

    private static async Task TerminateNativeHandleAsync(SandboxHandle handle, NativeHandleBinding binding, CancellationToken cancellationToken)
    {
        if (!NativeHandleIsLocal(binding)) return;
        var execution = binding.Receipt.Execution!;
        // Raw birth-key + boot validation refuses a known different process. Check/use is still not atomic;
        // pidfd / generation-bound containment is a separate platform requirement, not a claim made here.
        var state = ObserveNativeProcess(execution);
        if (state == SandboxRunState.Indeterminate) return;
        var terminationAttempted = state == SandboxRunState.Running;
        if (terminationAttempted) NativeProcess.KillSession(execution);
        var wait = Stopwatch.StartNew();
        while ((state = ObserveNativeProcess(execution)) == SandboxRunState.Running && wait.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        if (state != SandboxRunState.Gone) return;
        if (terminationAttempted && await WaitForSpoolsQuiescentAsync(handle, cancellationToken).ConfigureAwait(false)) await TryWriteLogSealAsync(handle, cancellationToken).ConfigureAwait(false);
        await TearDownIsolationAsync(handle).ConfigureAwait(false);
    }

    private static SandboxRunState ObserveNativeProcess(NativeProcessIdentity execution)
    {
        try { return NativeProcess.IsAlive(execution) ? SandboxRunState.Running : SandboxRunState.Gone; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { return SandboxRunState.Indeterminate; }
    }

    private static bool NativeHandleIsLocal(NativeHandleBinding binding) => string.Equals(binding.Request.Host, CurrentHost, StringComparison.OrdinalIgnoreCase) && binding.Request.BootId == NativeProcess.BootId;

    private sealed record NativeHandleBinding(NativeLaunchRecord Request, NativeLaunchReceipt Receipt);
}

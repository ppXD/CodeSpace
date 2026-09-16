using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using CodeSpace.Messages.Agents;
using CodeSpace.NativeLaunch;

namespace CodeSpace.Core.Services.Agents.Sandbox.Runners;

public sealed partial class LocalProcessRunner
{
    /// <summary>How long a terminate waits for the signalled tree to actually go away before it stops claiming anything about it.</summary>
    private static readonly TimeSpan NativeReapWait = TimeSpan.FromSeconds(5);

    // True + null is the explicit compatibility path: no reference and no visible registry. This cannot
    // distinguish pre-native handles from A-era handles whose registry was lost. A present reference never
    // takes that path; an A-era handle with a retained registry is validated without inventing a birth key.
    //
    // Every refusal names the gate that refused in <paramref name="refusal"/>. A terminate that skips is otherwise
    // indistinguishable from one that killed, and "the handle did not resolve" is far too coarse to act on: an
    // IOException re-reading the four receipt files is transient, while a spec-hash mismatch never will be.
    private static bool TryResolveNativeHandle(SandboxHandle handle, out NativeHandleBinding? binding, out string? refusal)
    {
        binding = null;
        refusal = null;
        try
        {
            var directory = NativeLaunchFiles.DirectoryFor(handle.SpoolDirectory);
            if (handle.NativeLaunch is null && !Path.Exists(directory)) return true;
            var request = NativeLaunchFiles.Read<NativeLaunchRecord>(directory, NativeLaunchProtocol.RequestFile);
            var receipt = NativeLaunchFiles.Read<NativeLaunchReceipt>(directory, NativeLaunchProtocol.ReceiptFile);
            var commitment = NativeLaunchFiles.Read<NativeLaunchCommitment>(directory, NativeLaunchProtocol.CommitmentFile);
            var execution = NativeLaunchFiles.Read<NativeProcessIdentity>(directory, NativeLaunchProtocol.ExecutionFile);
            if (request.Version != NativeLaunchProtocol.Version || string.IsNullOrWhiteSpace(request.Host) || string.IsNullOrWhiteSpace(request.SpecHash) || request.SpecHash != receipt.SpecHash || request.SpecHash != commitment.SpecHash) return Refuse(out refusal, "the launch request, receipt and commitment do not agree on the protocol version or spec hash");
            if (receipt.State is not ("ready" or "exited" or "stopped") || receipt.Broker is null || receipt.Execution is null || receipt.Broker != commitment.Broker || receipt.Execution != execution) return Refuse(out refusal, $"the receipt is not a completed start commitment (state '{receipt.State}')");
            if (string.IsNullOrWhiteSpace(execution.StartKey) || string.IsNullOrWhiteSpace(execution.BootId) || execution.BootId != request.BootId || receipt.Broker.BootId != request.BootId) return Refuse(out refusal, "the recorded execution identity is incomplete or names a different boot than the request");
            if (handle.Kind != LocalKind || handle.ProcessId != execution.ProcessId || handle.ProcessStartTimeUtc?.UtcTicks != execution.StartTimeUtcTicks) return Refuse(out refusal, $"the handle (kind '{handle.Kind}', pid {handle.ProcessId}) does not match the recorded execution (pid {execution.ProcessId})");
            if (!string.Equals(handle.LaunchHost, request.Host, StringComparison.OrdinalIgnoreCase) || handle.Deadline != request.Deadline || Path.GetFileName(handle.SpoolDirectory) != request.SpoolKey) return Refuse(out refusal, $"the handle (host '{handle.LaunchHost}') does not match the recorded request (host '{request.Host}')");
            if (handle.CgroupRunKey != receipt.CgroupRunKey || handle.EgressNetnsKey != receipt.EgressNetnsKey) return Refuse(out refusal, "the handle's isolation keys do not match the receipt's");
            if (handle.NativeLaunch is { } reference && (reference.Version != request.Version || reference.SpecHash != request.SpecHash || reference.Identity != request.Identity || reference.Execution != execution)) return Refuse(out refusal, "the handle's native-launch reference does not match the recorded launch");
            binding = new NativeHandleBinding(request, receipt);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException) { return Refuse(out refusal, $"reading the native-launch files threw {error.GetType().Name}: {error.Message}"); }
    }

    /// <summary>Refuse to bind the handle, recording WHY — one line per gate, so the reason survives to the caller instead of collapsing into a bare false.</summary>
    private static bool Refuse(out string? refusal, string reason)
    {
        refusal = reason;
        return false;
    }

    private static SandboxProbe ProbeNativeHandle(SandboxHandle handle, NativeHandleBinding binding)
    {
        // A bound terminal marker remains readable on shared storage. PID-derived answers require this boot.
        if (TryReadExitCode(Path.Combine(handle.SpoolDirectory, ExitMarkerFile), out var exitCode)) return new SandboxProbe { State = SandboxRunState.Exited, ExitCode = exitCode };
        if (!NativeHandleIsLocal(binding)) return new SandboxProbe { State = SandboxRunState.Indeterminate };
        return new SandboxProbe { State = ObserveNativeProcess(binding.Receipt.Execution!) };
    }

    /// <summary>
    /// Kill the supervised tree, and SAY what happened. Three of the returns here are a kill that was deliberately
    /// withheld and one is a kill whose effect was never observed; none of them throws, which is exactly why they were
    /// invisible to the abandon path before the result type existed.
    /// </summary>
    private static async Task<SandboxTerminateResult> TerminateNativeHandleAsync(SandboxHandle handle, NativeHandleBinding binding, CancellationToken cancellationToken)
    {
        if (!NativeHandleIsLocal(binding)) return SandboxTerminateResult.Skipped(SandboxTerminateOutcome.SkippedNotLocal, $"the launch was minted by host '{binding.Request.Host}' on boot '{binding.Request.BootId}'; this worker is '{CurrentHost}' on boot '{NativeProcess.BootId}'");

        var execution = binding.Receipt.Execution!;
        // Raw birth-key + boot validation refuses a known different process. Check/use is still not atomic;
        // pidfd / generation-bound containment is a separate platform requirement, not a claim made here.
        var state = ObserveNativeProcess(execution);

        if (state == SandboxRunState.Indeterminate) return IndeterminateTerminateResult(execution, "before any signal was issued");

        var terminationAttempted = state == SandboxRunState.Running;
        if (terminationAttempted) NativeProcess.KillSession(execution);
        var wait = Stopwatch.StartNew();
        while ((state = ObserveNativeProcess(execution)) == SandboxRunState.Running && wait.Elapsed < NativeReapWait) await Task.Delay(20, cancellationToken).ConfigureAwait(false);

        if (state == SandboxRunState.Indeterminate) return IndeterminateTerminateResult(execution, "after the kill signal was issued");
        if (state != SandboxRunState.Gone) return SandboxTerminateResult.Skipped(SandboxTerminateOutcome.TimedOutWaitingReap, $"pid {execution.ProcessId} was still alive {NativeReapWait.TotalSeconds:0.###}s after the kill signal; it may yet die, but nothing here observed it");

        if (terminationAttempted && await WaitForSpoolsQuiescentAsync(handle, cancellationToken).ConfigureAwait(false)) await TryWriteLogSealAsync(handle, cancellationToken).ConfigureAwait(false);

        await TearDownIsolationAsync(handle).ConfigureAwait(false);

        return terminationAttempted ? SandboxTerminateResult.Killed : SandboxTerminateResult.AlreadyGone;
    }

    /// <summary>Liveness itself could not be read, so no kill and no claim of one — <paramref name="when"/> says whether that was before or after the signal, which are different facts: one attempted nothing, the other attempted something whose effect is unknown.</summary>
    internal static SandboxTerminateResult IndeterminateTerminateResult(NativeProcessIdentity execution, string when) =>
        SandboxTerminateResult.Skipped(SandboxTerminateOutcome.SkippedIndeterminate, $"liveness of pid {execution.ProcessId} could not be observed {when}");

    private static SandboxRunState ObserveNativeProcess(NativeProcessIdentity execution)
    {
        try { return NativeProcess.IsAlive(execution) ? SandboxRunState.Running : SandboxRunState.Gone; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { return SandboxRunState.Indeterminate; }
    }

    private static bool NativeHandleIsLocal(NativeHandleBinding binding) => string.Equals(binding.Request.Host, CurrentHost, StringComparison.OrdinalIgnoreCase) && binding.Request.BootId == NativeProcess.BootId;

    private sealed record NativeHandleBinding(NativeLaunchRecord Request, NativeLaunchReceipt Receipt);
}

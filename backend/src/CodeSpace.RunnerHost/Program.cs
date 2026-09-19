using System.Diagnostics;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using CodeSpace.NativeLaunch;

namespace CodeSpace.RunnerHost;

internal static class Program
{
    private static readonly TimeSpan AdmissionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Deliberately NOT <c>async Task&lt;int&gt;</c>. The exec bootstrap must run <c>execve</c> on the process's own
    /// thread-group leader, and an async entry point leaves that thread blocked in the generated
    /// <c>GetAwaiter().GetResult()</c> for the whole run — so the exec landed on whatever thread-pool thread the
    /// admission handshake's last await resumed on. Blocking here explicitly is what gives that thread back.
    /// </summary>
    public static int Main(string[] args)
    {
        if (args is ["--protocol-version"]) { Console.WriteLine(NativeLaunchProtocol.Version); return 0; }
        if (args.Length != 2 || !Path.IsPathFullyQualified(args[1])) return 64;
        // Before any work, so that everything below — including a .NET unhandled-exception abort — says why into a
        // file that outlives this launch rather than into the app's stderr pipe, which is closed once the handle is built.
        if (args[0] is "broker" or "exec" or "guardian") NativeProcess.RedirectDiagnostics(Path.Combine(args[1], NativeLaunchProtocol.DiagnosticsFile));
        try
        {
            return args[0] switch
            {
                // Not a launch: the app's EgressSubnetAllocator asks this bootstrap whether a SECOND process is
                // refused an exclusive open, because an in-process second open cannot answer that on a Linux NFS
                // client (flock is emulated with per-PROCESS fcntl locks there). No spool, no receipt, no secrets.
                ExclusiveLockProbeChild.Argument => ExclusiveLockProbeChild.RunChild(args[1]),
                "broker" => BrokerAsync(args[1]).GetAwaiter().GetResult(),
                "exec" => Exec(args[1]),
                "guardian" => GuardianAsync(args[1]).GetAwaiter().GetResult(),
                _ => 64,
            };
        }
        catch (Exception error)
        {
            // Invocation/env/config exceptions may contain credentials. Only a type, never raw input, goes to stderr.
            NativeProcess.Explain(args[0], "rejected by " + error.GetType().Name);
            return 125;
        }
    }

    /// <summary>Say why this bootstrap is refusing, then refuse. Every <c>125</c> below is a workload that will never start, and one that exits without a word is indistinguishable from one the kernel killed.</summary>
    private static int Refuse(string mode, string reason)
    {
        NativeProcess.Explain(mode, "refused: " + reason);
        return 125;
    }

    private static async Task<int> BrokerAsync(string directory)
    {
        NativeProcess.NewSession();
        var request = ReadRequest(directory);
        var broker = NativeProcess.Current;
        if (!NativeLaunchFiles.TryCreate(directory, NativeLaunchProtocol.CommitmentFile, new NativeLaunchCommitment(request.SpecHash, broker)))
        {
            Console.WriteLine("discover");
            return 0;
        }

        var receipt = new NativeLaunchReceipt { SpecHash = request.SpecHash, Broker = broker, State = "committed" };
        Process? execution = null;
        Process? guardian = null;
        NativeProcessIdentity? executionIdentity = null;
        var released = false;
        var stage = "commitment-receipt";
        try
        {
            // Only the commitment winner may request the secret-bearing invocation / prepare isolation. This is
            // already an irreversible start slot, not an execution-success receipt. EOF cannot release a workload.
            NativeLaunchFiles.Replace(directory, NativeLaunchProtocol.ReceiptFile, receipt);
            Console.WriteLine("owned");
            await Console.Out.FlushAsync().ConfigureAwait(false);
            using var admission = new CancellationTokenSource(AdmissionTimeout);
            stage = "input";
            var invocation = await NativeLaunchFiles.ReadFrameAsync<NativeLaunchInvocation>(Console.OpenStandardInput(), admission.Token).ConfigureAwait(false);
            var spec = invocation.Spec with { ReadOnlyPaths = invocation.ReadOnlyPaths, CaptureBudget = invocation.CaptureBudget };
            if (NativeLaunchProtocol.SpecHash(NativeLaunchProtocol.Freeze(spec)) != request.SpecHash) throw new InvalidDataException("Invocation binding mismatch.");
            if (DateTimeOffset.UtcNow >= request.Deadline) throw new TimeoutException("Admission deadline elapsed.");

            execution = StartInfo("exec", directory, redirectInput: true);
            stage = "exec-identity";
            await ProcessLaunchThread.StartAsync(execution, admission.Token).ConfigureAwait(false);
            await NativeLaunchFiles.WriteFrameAsync(execution.StandardInput.BaseStream, invocation, admission.Token).ConfigureAwait(false);
            executionIdentity = await WaitIdentityAsync(directory, NativeLaunchProtocol.ExecutionFile, execution, admission.Token).ConfigureAwait(false);

            guardian = StartInfo("guardian", directory, redirectInput: false);
            stage = "guardian-identity";
            await ProcessLaunchThread.StartAsync(guardian, admission.Token).ConfigureAwait(false);
            var guardianIdentity = await WaitIdentityAsync(directory, NativeLaunchProtocol.GuardianFile, guardian, admission.Token).ConfigureAwait(false);
            receipt = receipt with { State = "ready", Execution = executionIdentity, Guardian = guardianIdentity, EgressNetnsKey = invocation.EgressNetnsKey, CgroupRunKey = invocation.CgroupRunKey, Confinement = invocation.Confinement };
            stage = "ready-receipt";
            // The complete discoverable receipt is durable BEFORE the exec bootstrap is released. If the following
            // write/exec ACK is lost, replay discovers this identity; it never consumes another start commitment.
            NativeLaunchFiles.Replace(directory, NativeLaunchProtocol.ReceiptFile, receipt);
            stage = "release";
            released = true; // A failed write/flush may have delivered the byte. Its outcome is indeterminate, never "rejected before exec".
            await execution.StandardInput.BaseStream.WriteAsync(new byte[] { 1 }, admission.Token).ConfigureAwait(false);
            await execution.StandardInput.BaseStream.FlushAsync(admission.Token).ConfigureAwait(false);
            execution.StandardInput.Close();
            stage = "observation";

            while (!execution.HasExited)
            {
                if (DateTimeOffset.UtcNow >= request.Deadline) { Stop(directory, executionIdentity, "deadline"); break; }
                if (guardian.HasExited && !execution.HasExited) { Stop(directory, executionIdentity, "guardian-lost"); break; }
                await Task.Delay(50).ConfigureAwait(false);
            }
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await execution.WaitForExitAsync(drain.Token).ConfigureAwait(false);
            receipt = receipt with { State = File.Exists(NativeLaunchFiles.PathFor(directory, NativeLaunchProtocol.StopFile)) ? "stopped" : "exited" };
            NativeLaunchFiles.Replace(directory, NativeLaunchProtocol.ReceiptFile, receipt);
            return 0;
        }
        catch (Exception error)
        {
            if (executionIdentity is not null) Stop(directory, executionIdentity, "bootstrap-failed");
            try { NativeLaunchFiles.Replace(directory, NativeLaunchProtocol.ReceiptFile, receipt with { State = released ? "indeterminate" : "rejected", Problem = stage + ":" + error.GetType().Name }); }
            catch (IOException) { }
            throw;
        }
        finally
        {
            // Close an incomplete frame/release pipe before cleanup. A bootstrap without a complete release exits
            // without exec. The guardian owns cleanup if this broker is killed instead of entering this finally.
            if (execution is not null)
            {
                try { execution.StandardInput.Close(); } catch (InvalidOperationException) { }
                if (executionIdentity is not null) NativeProcess.KillSession(executionIdentity);
                execution.Dispose();
            }
            if (guardian is not null)
            {
                using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await guardian.WaitForExitAsync(finish.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { guardian.Kill(); }
                guardian.Dispose();
            }
        }
    }

    /// <summary>
    /// Become the workload — ON THIS THREAD, which is the process's thread-group leader.
    ///
    /// <para>Why that matters, measured on Linux CI: <c>execve</c> from a thread that is NOT the leader makes the
    /// kernel run <c>de_thread()</c>, which kills the other threads and WAITS for the original leader to become a
    /// zombie before the caller can take over its pid. For the whole of that window — unbounded, and longer the more
    /// loaded the host — <c>/proc/&lt;pid&gt;/stat</c> reports the run's supervised pid as state <c>Z</c> with the
    /// bootstrap's own comm. Every liveness read in this product treats <c>Z</c> as gone, so a run caught in the act
    /// of BECOMING its workload was observed dead: the probe answered Gone, the observer landed it Failed, and the
    /// reconciler abandoned it as process-confirmed-dead without killing anything — leaving the live agent running
    /// against a workspace the database calls Failed.</para>
    ///
    /// <para>The leader exec's without that dance: it still waits for its siblings, but it never zombies, so the pid
    /// stays observably alive and its identity survives the exec exactly as the protocol claims.</para>
    /// </summary>
    private static int Exec(string directory)
    {
        var admitted = AdmitExecutionAsync(directory).GetAwaiter().GetResult();

        if (admitted.Invocation is null || admitted.Broker is null) return admitted.Refusal;

        // Same PID/session/start identity across exec; no process-start-to-handle-persist window exists here.
        // PDEATHSIG is thread-specific, so it is applied on the exact thread executing execve as well as before
        // the initial pipe wait — which is now this one, the leader, for the reason above.
        NativeProcess.ProtectFromParentDeath(admitted.Broker);
        NativeProcess.Explain("exec", $"execve from {NativeProcess.ExecThread()} carrying birth key {admitted.BirthKey}");
        return NativeProcess.Exec(admitted.Invocation);
    }

    /// <summary>Everything the exec bootstrap must agree with its broker about before it may become the workload. It returns that agreement rather than acting on it, so the act itself happens on the caller's thread.</summary>
    private static async Task<ExecAdmission> AdmitExecutionAsync(string directory)
    {
        NativeProcess.NewSession();
        var request = ReadRequest(directory);
        var commitment = NativeLaunchFiles.Read<NativeLaunchCommitment>(directory, NativeLaunchProtocol.CommitmentFile);
        NativeProcess.ProtectFromParentDeath(commitment.Broker);
        using var admission = new CancellationTokenSource(AdmissionTimeout);
        var input = Console.OpenStandardInput();
        var invocation = await NativeLaunchFiles.ReadFrameAsync<NativeLaunchInvocation>(input, admission.Token).ConfigureAwait(false);
        if (NativeLaunchProtocol.SpecHash(invocation.Spec with { ReadOnlyPaths = invocation.ReadOnlyPaths, CaptureBudget = invocation.CaptureBudget }) != request.SpecHash) throw new InvalidDataException("Exec binding mismatch.");
        if (!NativeLaunchFiles.TryCreate(directory, NativeLaunchProtocol.ExecutionFile, NativeProcess.Current)) throw new IOException("The physical execution slot has already been used.");
        var release = new byte[1];
        await input.ReadExactlyAsync(release, admission.Token).ConfigureAwait(false);
        if (release[0] != 1) return RefuseExec("the byte on the private pipe is not this protocol's release");
        if (!NativeProcess.IsAlive(commitment.Broker)) return RefuseExec($"the launch broker (pid {commitment.Broker.ProcessId}) was gone at the release");
        if (DateTimeOffset.UtcNow >= request.Deadline) return RefuseExec("the launch deadline elapsed before the release");
        var receipt = NativeLaunchFiles.Read<NativeLaunchReceipt>(directory, NativeLaunchProtocol.ReceiptFile);
        if (receipt.State != "ready" || receipt.SpecHash != request.SpecHash || !NativeProcess.Same(receipt.Broker, commitment.Broker) || receipt.Execution is null || !NativeProcess.Same(receipt.Execution, NativeProcess.Current))
            return RefuseExec($"the committed receipt (state '{receipt.State}') does not release THIS execution");
        if (receipt.Guardian is null || !NativeProcess.IsAlive(receipt.Guardian)) return RefuseExec($"the guardian (pid {receipt.Guardian?.ProcessId}) was gone at the release");

        return new ExecAdmission(invocation, commitment.Broker, receipt.Execution.StartKey);
    }

    private static ExecAdmission RefuseExec(string reason) => new(null, null, null, Refuse("exec", reason));

    /// <summary>The admission handshake's outcome: the invocation this bootstrap is released to become plus the broker it stays bound to, or the exit code it refuses with.</summary>
    private sealed record ExecAdmission(NativeLaunchInvocation? Invocation, NativeProcessIdentity? Broker, string? BirthKey, int Refusal = 0);

    private static async Task<int> GuardianAsync(string directory)
    {
        NativeProcess.NewSession();
        var request = ReadRequest(directory);
        var commitment = NativeLaunchFiles.Read<NativeLaunchCommitment>(directory, NativeLaunchProtocol.CommitmentFile);
        var execution = NativeLaunchFiles.Read<NativeProcessIdentity>(directory, NativeLaunchProtocol.ExecutionFile);
        if (!NativeProcess.IsAlive(commitment.Broker) || !NativeProcess.IsAlive(execution)) return Refuse("guardian", $"the broker (pid {commitment.Broker.ProcessId}) or the execution (pid {execution.ProcessId}) was already gone");
        if (!NativeLaunchFiles.TryCreate(directory, NativeLaunchProtocol.GuardianFile, NativeProcess.Current)) return Refuse("guardian", "another guardian already owns this launch");
        while (NativeProcess.IsAlive(execution))
        {
            if (!NativeProcess.IsAlive(commitment.Broker)) { Stop(directory, execution, "broker-lost"); return 0; }
            if (DateTimeOffset.UtcNow >= request.Deadline) { Stop(directory, execution, "deadline"); return 0; }
            await Task.Delay(50).ConfigureAwait(false);
        }
        // Linux may deliver PDEATHSIG to the session leader before this watcher polls. Its remaining process
        // group still belongs to this recorded session; clear it on broker loss even after leader exit.
        if (!NativeProcess.IsAlive(commitment.Broker)) Stop(directory, execution, "broker-lost");
        return 0;
    }

    private static NativeLaunchRecord ReadRequest(string directory)
    {
        var request = NativeLaunchFiles.Read<NativeLaunchRecord>(directory, NativeLaunchProtocol.RequestFile);
        if (request.Version != NativeLaunchProtocol.Version || request.BootId != NativeProcess.BootId) throw new InvalidDataException("Native launch belongs to another protocol or boot.");
        return request;
    }

    private static Process StartInfo(string mode, string directory, bool redirectInput)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, NativeLaunchProtocol.BinaryName)) { UseShellExecute = false, RedirectStandardInput = redirectInput, CreateNoWindow = true };
        info.ArgumentList.Add(mode); info.ArgumentList.Add(directory);
        return new Process { StartInfo = info };
    }

    private static async Task<NativeProcessIdentity> WaitIdentityAsync(string directory, string file, Process process, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var identity = NativeLaunchFiles.Read<NativeProcessIdentity>(directory, file);
                if (!NativeProcess.Same(identity, NativeProcess.Identify(process.Id))) throw new InvalidDataException("Bootstrap identity mismatch.");
                return identity;
            }
            catch (FileNotFoundException) { }
            catch (System.Text.Json.JsonException) when (!process.HasExited) { }
            if (process.HasExited) throw new IOException("Bootstrap exited before publishing its identity.");
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Stop(string directory, NativeProcessIdentity execution, string reason)
    {
        NativeProcess.Explain("controller", $"stopping the execution (pid {execution.ProcessId}): {reason}");
        try { NativeLaunchFiles.TryCreate(directory, NativeLaunchProtocol.StopFile, new NativeLaunchStop(reason, DateTimeOffset.UtcNow)); }
        finally { NativeProcess.KillSession(execution); }
    }
}

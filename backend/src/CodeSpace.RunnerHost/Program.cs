using System.Diagnostics;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using CodeSpace.NativeLaunch;

namespace CodeSpace.RunnerHost;

internal static class Program
{
    private static readonly TimeSpan AdmissionTimeout = TimeSpan.FromSeconds(30);

    public static async Task<int> Main(string[] args)
    {
        if (args is ["--protocol-version"]) { Console.WriteLine(NativeLaunchProtocol.Version); return 0; }
        if (args.Length != 2 || !Path.IsPathFullyQualified(args[1])) return 64;
        try
        {
            return args[0] switch
            {
                // Not a launch: the app's EgressSubnetAllocator asks this bootstrap whether a SECOND process is
                // refused an exclusive open, because an in-process second open cannot answer that on a Linux NFS
                // client (flock is emulated with per-PROCESS fcntl locks there). No spool, no receipt, no secrets.
                ExclusiveLockProbeChild.Argument => ExclusiveLockProbeChild.RunChild(args[1]),
                "broker" => await BrokerAsync(args[1]).ConfigureAwait(false),
                "exec" => await ExecAsync(args[1]).ConfigureAwait(false),
                "guardian" => await GuardianAsync(args[1]).ConfigureAwait(false),
                _ => 64,
            };
        }
        catch (Exception error)
        {
            // Invocation/env/config exceptions may contain credentials. Only a type, never raw input, goes to stderr.
            Console.Error.WriteLine("Native launch rejected: " + error.GetType().Name);
            return 125;
        }
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

    private static async Task<int> ExecAsync(string directory)
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
        if (release[0] != 1 || !NativeProcess.IsAlive(commitment.Broker) || DateTimeOffset.UtcNow >= request.Deadline) return 125;
        var receipt = NativeLaunchFiles.Read<NativeLaunchReceipt>(directory, NativeLaunchProtocol.ReceiptFile);
        if (receipt.State != "ready" || receipt.SpecHash != request.SpecHash || !NativeProcess.Same(receipt.Broker, commitment.Broker) || receipt.Execution is null || !NativeProcess.Same(receipt.Execution, NativeProcess.Current) || receipt.Guardian is null || !NativeProcess.IsAlive(receipt.Guardian)) return 125;
        // Same PID/session/start identity across exec; no process-start-to-handle-persist window exists here.
        // Async IO can resume on another native thread. PDEATHSIG is thread-specific; apply it on the exact
        // thread executing execve as well as before the initial pipe wait.
        NativeProcess.ProtectFromParentDeath(commitment.Broker);
        return NativeProcess.Exec(invocation);
    }

    private static async Task<int> GuardianAsync(string directory)
    {
        NativeProcess.NewSession();
        var request = ReadRequest(directory);
        var commitment = NativeLaunchFiles.Read<NativeLaunchCommitment>(directory, NativeLaunchProtocol.CommitmentFile);
        var execution = NativeLaunchFiles.Read<NativeProcessIdentity>(directory, NativeLaunchProtocol.ExecutionFile);
        if (!NativeProcess.IsAlive(commitment.Broker) || !NativeProcess.IsAlive(execution)) return 125;
        if (!NativeLaunchFiles.TryCreate(directory, NativeLaunchProtocol.GuardianFile, NativeProcess.Current)) return 125;
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
        try { NativeLaunchFiles.TryCreate(directory, NativeLaunchProtocol.StopFile, new NativeLaunchStop(reason, DateTimeOffset.UtcNow)); }
        finally { NativeProcess.KillSession(execution); }
    }
}

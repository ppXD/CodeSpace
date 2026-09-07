using System.Diagnostics;
using System.Text;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Sandbox.Runners;

public sealed partial class LocalProcessRunner
{
    private sealed record BoundedCommandRequest(SandboxSpec Spec, Func<string, CancellationToken, Task>? OnStdoutLine);
    private sealed record CommandStreamRead(Stream Source, CommandStreamCapture Capture, Func<string, CancellationToken, Task>? OnLine = null);

    private async Task<SandboxResult> RunWithBoundedCaptureAsync(BoundedCommandRequest request, CancellationToken cancellationToken)
    {
        var spec = request.Spec;
        var budget = spec.CaptureBudget!;
        ValidateCaptureBudget(budget);
        await using var invocation = await PrepareCommandAsync(spec, cancellationToken).ConfigureAwait(false);
        using var process = new Process { StartInfo = invocation.StartInfo };
        await ProcessLaunchThread.StartAsync(process, cancellationToken).ConfigureAwait(false);
        using var pipes = new CommandPipeLifetime(process, _logger);
        using var timeout = WallClockCts(spec.TimeoutSeconds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var stdout = new CommandStreamCapture(request.OnStdoutLine is null ? budget.StdoutBytes : 0, request.OnStdoutLine is not null);
        var stderr = new CommandStreamCapture(budget.StderrBytes, false);
        var stdoutRead = new CommandStreamRead(process.StandardOutput.BaseStream, stdout, request.OnStdoutLine);
        var stdoutTask = request.OnStdoutLine is null ? ReadCaptureAsync(stdoutRead, pipes.Token) : PumpBoundedStdoutAsync(stdoutRead, budget.StdoutLineBytes, linked.Token, NoProgressWindow());
        var stderrTask = ReadCaptureAsync(new CommandStreamRead(process.StandardError.BaseStream, stderr), pipes.Token);
        try
        {
            // Keep stdout faults observable while the process is alive (a callback fault must terminate it).
            if (request.OnStdoutLine is not null) await stdoutTask.ConfigureAwait(false);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(linked.Token).ConfigureAwait(false);
            return Result(invocation.ExitStatus(process.ExitCode), process.ExitCode);
        }
        catch (AgentStalledException)
        {
            await TerminateAndDrainAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return Result(SandboxStatus.Stalled, -1);
        }
        catch (OperationCanceledException)
        {
            await TerminateAndDrainAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return Result(SandboxStatus.TimedOut, -1);
        }
        catch
        {
            await TerminateAndDrainAsync().ConfigureAwait(false);
            throw;
        }

        async Task TerminateAndDrainAsync()
        {
            KillQuietly(process);
            await Task.WhenAll(SafeObserveCaptureAsync(stdoutTask), SafeObserveCaptureAsync(stderrTask)).ConfigureAwait(false);
        }

        SandboxResult Result(SandboxStatus status, int exitCode)
        {
            var output = stdout.Snapshot();
            var error = stderr.Snapshot();
            if (output.Observation.DeliveryComplete is false)
                _logger.LogWarning("Command stdout delivery is incomplete: one or more observed records were not delivered completely; observed {ObservedBytes} bytes", output.Observation.ObservedBytes);
            return new SandboxResult { Status = status, ExitCode = exitCode, Stdout = output.Text, Stderr = error.Text, Observation = new SandboxObservation { Stdout = output.Observation, Stderr = error.Observation } };
        }
    }

    private static void ValidateCaptureBudget(SandboxCaptureBudget budget)
    {
        if (budget.StdoutBytes is < 0 or > SandboxCaptureBudget.MaximumBytes || budget.StderrBytes is < 0 or > SandboxCaptureBudget.MaximumBytes || budget.StdoutLineBytes is < 0 or > SandboxCaptureBudget.MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(budget), $"Capture budgets must be between 0 and {SandboxCaptureBudget.MaximumBytes} bytes.");
    }

    private static async Task ReadCaptureAsync(CommandStreamRead read, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        while (true)
        {
            var count = await read.Source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (count == 0) { read.Capture.EndOfStream(); return; }
            read.Capture.Observe(buffer.AsSpan(0, count));
        }
    }

    private static async Task PumpBoundedStdoutAsync(CommandStreamRead read, int lineBudget, CancellationToken cancellationToken, TimeSpan? idle)
    {
        var buffer = new byte[8192];
        var line = new byte[lineBudget];
        var retained = 0;
        var omitted = false;
        while (true)
        {
            int count;
            using (var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (idle is { } duration) window.CancelAfter(duration);
                try { count = await read.Source.ReadAsync(buffer, window.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new AgentStalledException(); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (count == 0)
            {
                read.Capture.EndOfStream();
                if (retained > 0 && !omitted) await DeliverAsync(retained).ConfigureAwait(false);
                return;
            }
            read.Capture.Observe(buffer.AsSpan(0, count));
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] != '\n')
                {
                    if (retained < line.Length) line[retained++] = buffer[index];
                    else { omitted = true; read.Capture.OmitDelivery(); }
                    continue;
                }
                if (!omitted) await DeliverAsync(retained > 0 && line[retained - 1] == '\r' ? retained - 1 : retained).ConfigureAwait(false);
                retained = 0;
                omitted = false;
            }
        }

        async Task DeliverAsync(int length)
        {
            string text;
            try { text = StrictUtf8.GetString(line, 0, length); }
            catch (DecoderFallbackException) { read.Capture.OmitDelivery(); return; }
            var delivery = read.OnLine!(text, cancellationToken);
            try { await delivery.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch
            {
                read.Capture.OmitDelivery();
                ObserveLateFault(delivery);
                throw;
            }
        }
    }

    private async Task SafeObserveCaptureAsync(Task readTask)
    {
        try { await readTask.WaitAsync(TerminationDrainTimeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            _logger.LogWarning("Command capture did not reach EOF within termination grace; captured bytes are a lower bound");
            ObserveLateFault(readTask);
        }
        catch { /* preserve the command/callback outcome; the snapshot keeps observed bytes without inventing EOF */ }
    }

    private static void ObserveLateFault(Task task) =>
        _ = task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    /// <summary>One fixed-capacity byte prefix. Snapshot and reads may race during bounded termination drain; the lock makes every returned byte/count/EOF tuple consistent.</summary>
    private sealed class CommandStreamCapture(int capacity, bool streaming)
    {
        private readonly byte[] _prefix = new byte[capacity];
        private readonly object _gate = new();
        private long _observed;
        private int _retained;
        private bool _eof;
        private bool? _deliveryComplete = streaming ? true : null;

        public void Observe(ReadOnlySpan<byte> bytes)
        {
            lock (_gate)
            {
                _observed += bytes.Length;
                var keep = Math.Min(bytes.Length, _prefix.Length - _retained);
                bytes[..keep].CopyTo(_prefix.AsSpan(_retained));
                _retained += keep;
            }
        }

        public void EndOfStream() { lock (_gate) _eof = true; }
        public void OmitDelivery() { lock (_gate) _deliveryComplete = false; }

        public (string Text, SandboxStreamObservation Observation) Snapshot()
        {
            lock (_gate)
            {
                var complete = _retained == _observed;
                string text;
                try { text = StrictUtf8.GetString(_prefix, 0, _retained); }
                catch (DecoderFallbackException) { complete = false; text = Encoding.UTF8.GetString(_prefix, 0, _retained); }
                return (text, new SandboxStreamObservation { ObservedBytes = _observed, ReachedEndOfStream = _eof, CaptureComplete = complete, DeliveryComplete = _deliveryComplete });
            }
        }
    }
}

using System.Diagnostics;
using System.Threading.Channels;

namespace CodeSpace.Core.Services.Agents.Sandbox.Runners;

/// <summary>Linux parent-death signals follow the creating native thread. Keep that thread alive for the host lifetime, even when an async caller's managed thread retires.</summary>
internal static class ProcessLaunchThread
{
    private static readonly Lazy<Channel<LaunchRequest>> Requests = new(CreateLauncher);

    public static async Task StartAsync(Process process, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux()) { process.Start(); return; }

        var request = new LaunchRequest(process, cancellationToken);
        await Requests.Value.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        // Once queued, the launch result must be observed: cancellation after Process.Start cannot leave an
        // unowned child. The runner's existing cancellation path receives and terminates any started process.
        await request.Started.Task.ConfigureAwait(false);
    }

    private static Channel<LaunchRequest> CreateLauncher()
    {
        var requests = Channel.CreateBounded<LaunchRequest>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        var thread = new Thread(() => Run(requests.Reader)) { IsBackground = true, Name = "CodeSpace process launcher" };
        thread.Start();
        return requests;
    }

    private static void Run(ChannelReader<LaunchRequest> requests)
    {
        // This thread deliberately lives until host exit; bwrap still dies with its parent. It does not wait
        // for children, serialize their execution, or retain completed Process instances.
        while (true)
        {
            var request = requests.ReadAsync().AsTask().GetAwaiter().GetResult();
            if (request.CancellationToken.IsCancellationRequested)
            {
                request.Started.TrySetCanceled(request.CancellationToken);
                continue;
            }
            try
            {
                request.Process.Start();
                request.Started.TrySetResult(true);
            }
            catch (Exception error) { request.Started.TrySetException(error); }
        }
    }

    private sealed record LaunchRequest(Process Process, CancellationToken CancellationToken)
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

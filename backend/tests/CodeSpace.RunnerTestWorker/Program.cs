using System.Text.Json;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;

namespace CodeSpace.RunnerTestWorker;

/// <summary>A real OS observer. It deliberately exits immediately after launch, before any application handle persistence.</summary>
public static class RunnerTestWorker
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 3) return 64;
        var request = JsonSerializer.Deserialize<SandboxLaunchRequest>(await File.ReadAllTextAsync(args[0]), NativeLaunchProtocol.Json) ?? throw new InvalidDataException();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!File.Exists(args[1])) await Task.Delay(10, deadline.Token);
        var handle = await new LocalProcessRunner().LaunchOrDiscoverAsync(request, deadline.Token);
        Console.WriteLine(JsonSerializer.Serialize(handle, NativeLaunchProtocol.Json));
        await Console.Out.FlushAsync();
        if (args[2] == "crash") Environment.Exit(86);
        return 0;
    }
}

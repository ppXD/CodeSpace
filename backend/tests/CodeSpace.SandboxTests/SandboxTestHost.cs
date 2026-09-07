using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;

namespace CodeSpace.SandboxTests;

internal static class SandboxTestHost
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--lifetime-host") return 2;
        var result = await new LocalProcessRunner().RunAsync(new SandboxSpec
        {
            Command = "/bin/sh", Args = new[] { "-c", "touch ready; while :; do printf x >> pulse; sleep 0.1; done" }, WorkingDirectory = args[1], TimeoutSeconds = 60,
        }, CancellationToken.None);
        return result.ExitCode;
    }
}

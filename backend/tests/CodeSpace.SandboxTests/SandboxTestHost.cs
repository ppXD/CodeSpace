using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using System.Diagnostics;

namespace CodeSpace.SandboxTests;

internal static class SandboxTestHost
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--durable-observer", var directory, var timeout])
        {
            var handle = await new LocalProcessRunner().LaunchAsync(NativeLaunchIsolationE2ETests.Spec(directory, int.Parse(timeout)), Path.GetFileName(directory), CancellationToken.None);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(handle, NativeLaunchProtocol.Json));
            await Console.Out.FlushAsync();
            Environment.Exit(86);
        }
        if (args is ["--native-launch-smoke"])
        {
            await using (var test = new NativeLaunchIsolationE2ETests()) await test.Observer_exit_does_not_stop_the_confined_process_and_broker_death_does();
            Console.WriteLine("PASS observer-exit / broker-death / discover");
            await using (var test = new NativeLaunchIsolationE2ETests()) await test.Parent_death_protection_stops_the_confined_workload_even_while_the_guardian_is_suspended();
            Console.WriteLine("PASS kernel parent-death with suspended guardian");
            await using (var test = new NativeLaunchIsolationE2ETests()) await test.The_execution_deadline_remains_enforced_with_no_observer_process();
            Console.WriteLine("PASS observer-independent execution deadline");
            return 0;
        }
        if (args is ["--native-compatibility-smoke"])
        {
            var lifecycle = new GitWorkspaceIsolationE2ETests();
            await lifecycle.Killing_the_worker_process_still_terminates_its_confined_command();
            await lifecycle.A_command_survives_its_managed_launch_thread_while_the_worker_process_is_alive(stream: false);
            await lifecycle.A_command_survives_its_managed_launch_thread_while_the_worker_process_is_alive(stream: true);
            Console.WriteLine("PASS existing worker-death / batch launch-thread / stream launch-thread");
            using var confinement = new BubblewrapConfinementSandboxTests();
            await confinement.Bubblewrap_confines_the_agent_to_its_workspace_and_blocks_operator_secrets();
            await confinement.Bubblewrap_severs_egress_when_network_is_disallowed();
            await confinement.Bubblewrap_drops_every_capability();
            await confinement.Caps_processes_and_file_size_via_prlimit_inherited_by_the_agent();
            await confinement.Caps_a_runaway_file_write_so_it_cannot_fill_the_disk();
            Console.WriteLine("PASS existing workspace/secret isolation / network / capabilities / resource limits / file cap");
            return 0;
        }
        if (args.Length != 2 || args[0] != "--lifetime-host") return 2;
        var result = await new LocalProcessRunner().RunAsync(new SandboxSpec
        {
            Command = "/bin/sh", Args = new[] { "-c", "touch ready; while :; do printf x >> pulse; sleep 0.1; done" }, WorkingDirectory = args[1], TimeoutSeconds = 60,
        }, CancellationToken.None);
        return result.ExitCode;
    }

    internal static ProcessStartInfo StartSelf(params string[] args)
    {
        var executable = Environment.GetEnvironmentVariable("CODESPACE_SANDBOX_TEST_HOST");
        var info = new ProcessStartInfo(executable ?? "dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (executable is null) info.ArgumentList.Add(typeof(SandboxTestHost).Assembly.Location);
        foreach (var argument in args) info.ArgumentList.Add(argument);
        return info;
    }
}

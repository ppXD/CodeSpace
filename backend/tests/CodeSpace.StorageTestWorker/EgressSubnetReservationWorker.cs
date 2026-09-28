using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Settings;
using Microsoft.Extensions.Configuration;

namespace CodeSpace.StorageTestWorker;

/// <summary>A second worker PROCESS reserving per-run /30s through the production host allocator under a spool root the parent names, and holding them until told to exit. Barriers on stdin/stdout, so two of these race on the parent's word rather than on a timer.</summary>
public static class EgressSubnetReservationWorker
{
    public const string Mode = "egress-subnets";

    public static int Run(string spoolRoot, int count)
    {
        RuntimeSettings.Bind(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Agents:RunSpoolDirectory"] = spoolRoot }).Build());

        Barrier("ready", "go");

        for (var i = 0; i < count; i++) Console.WriteLine(EgressSubnetAllocator.Host.Acquire(Guid.NewGuid().ToString("N")).Cidr);

        Barrier("held", "continue");

        return 0;
    }

    private static void Barrier(string reached, string awaited)
    {
        Console.WriteLine(reached);

        if (Console.ReadLine() != awaited) throw new IOException($"The parent closed the {reached} barrier.");
    }
}

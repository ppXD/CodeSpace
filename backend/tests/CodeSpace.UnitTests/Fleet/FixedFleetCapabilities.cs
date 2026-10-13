using CodeSpace.Core.Services.Fleet;

namespace CodeSpace.UnitTests.Fleet;

/// <summary>A fleet whose answer the test fixes: <c>true</c> is a fleet of current workers, <c>false</c> one with a previous-version worker still live. Counts the asks so a test can prove the gate is consulted only when something would be sealed.</summary>
public sealed class FixedFleetCapabilities(bool supported) : IFleetCapabilities
{
    public static FixedFleetCapabilities AllCurrent => new(true);

    public static FixedFleetCapabilities WithPreviousVersionWorker => new(false);

    public int Asks { get; private set; }

    public bool AllWorkersSupport(string capability)
    {
        Asks++;
        return supported;
    }
}

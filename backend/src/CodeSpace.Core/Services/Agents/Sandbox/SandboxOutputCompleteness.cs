using CodeSpace.Core.Services.Agents.Sandbox.Exceptions;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Sandbox;

/// <summary>Opt-in full-output consumer contract. Legacy producers without observation retain their existing contract; explicit incomplete captures can never masquerade as complete text.</summary>
public static class SandboxOutputCompleteness
{
    public static void RequireStdout(SandboxResult result) => Require(result, result.Observation?.Stdout, "stdout");
    public static void RequireStderr(SandboxResult result) => Require(result, result.Observation?.Stderr, "stderr");

    private static void Require(SandboxResult result, SandboxStreamObservation? observation, string stream)
    {
        if (observation is not null && !(observation.ReachedEndOfStream && observation.CaptureComplete))
            throw new IncompleteSandboxOutputException(result, stream);
    }
}

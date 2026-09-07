using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Sandbox.Exceptions;

/// <summary>Observation cannot support a full-output parse. The original execution receipt is retained; retrying the command could duplicate its side effects.</summary>
public sealed class IncompleteSandboxOutputException(SandboxResult result, string stream) : Exception($"Command {stream} observation is incomplete; a complete-output consumer cannot parse it. The command execution outcome is preserved and must not be replayed automatically."), IFailure
{
    public SandboxResult Result { get; } = result;
    public string Stream { get; } = stream;
    public FailureKind Kind => FailureKind.Unprocessable;
    public string Code => "sandbox_output_incomplete";
}

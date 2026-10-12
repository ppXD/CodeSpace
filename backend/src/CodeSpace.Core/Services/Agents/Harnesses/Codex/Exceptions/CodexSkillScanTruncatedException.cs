using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Harnesses.Codex.Exceptions;

/// <summary>
/// A Codex task that loads no repository instructions was refused before a sandbox was provisioned for it: a skill folder
/// of its checkout holds more directories than the Codex CLI reads under one root (<c>CodexRepositorySkills</c>). Past
/// that limit which skills the CLI loads depends on the order it reads the directories in, so the harness cannot name
/// every one it would load to switch it off, and running the task half switched off would let the checkout's author put
/// a skill in front of the model. Every attempt over the same checkout is refused alike.
/// </summary>
public sealed class CodexSkillScanTruncatedException(string message) : Exception(message), IFailure
{
    public FailureKind Kind => FailureKind.Unprocessable;

    public string Code => FailureCodes.CodexSkillScanTruncated;
}

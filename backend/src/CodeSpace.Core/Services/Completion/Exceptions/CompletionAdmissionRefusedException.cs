using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Completion.Exceptions;

/// <summary>
/// A definition's completion opt-in refused to launch: <c>enforced</c> on an operating mode without Enforceable
/// standing, or a value outside the closed vocabulary. A property of the definition, never of the infrastructure —
/// relaunching it unchanged refuses again, so it carries the same kind and code the save-time validator answers the
/// same rule with.
///
/// <para>Typed so a caller that launches many definitions in one transaction can tell this refusal from a real
/// failure: the webhook dispatcher and the schedule producer isolate it to the one activation it belongs to and let
/// every sibling start, while anything else still rolls the delivery or tick back. Still an
/// <see cref="InvalidOperationException"/>, so every existing catch reads it exactly as before.</para>
/// </summary>
public sealed class CompletionAdmissionRefusedException(string message) : InvalidOperationException(message), IFailure
{
    public FailureKind Kind => FailureKind.Unprocessable;

    public string Code => FailureCodes.WorkflowDefinitionInvalid;
}

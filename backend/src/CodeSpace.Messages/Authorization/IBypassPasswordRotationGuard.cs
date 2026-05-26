namespace CodeSpace.Messages.Authorization;

/// <summary>
/// Marker — request is allowed through even when the current user has
/// password_must_change=true. The only legitimate implementor is ChangePasswordCommand
/// itself; without this, the rotation gate would reject the call that lifts it.
///
/// Adding a new bypass MUST be reviewed: anything that runs while the gate is on can
/// be issued by a caller using known bootstrap credentials.
/// </summary>
public interface IBypassPasswordRotationGuard
{
}

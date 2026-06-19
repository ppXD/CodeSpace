namespace CodeSpace.Messages.Enums;

/// <summary>
/// How a <c>ModelCredentialModel</c> row got onto its credential's model list: typed by an operator
/// (<see cref="Manual"/>) or discovered by reflecting the provider's model endpoint (<see cref="Reflected"/>).
/// The refresh UPSERT branches on this — a refresh re-writes <see cref="Reflected"/> rows but NEVER touches a
/// <see cref="Manual"/> one, so an operator's hand-entered custom model is safe from being clobbered.
/// </summary>
public enum ModelSource
{
    Manual,
    Reflected
}

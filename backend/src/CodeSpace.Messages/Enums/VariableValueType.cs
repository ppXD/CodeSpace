namespace CodeSpace.Messages.Enums;

/// <summary>
/// Value-kind discriminator for a <c>variable</c> row. Drives which column carries the
/// data and how the engine treats it at scope-build / replay time:
///   • <see cref="Secret"/>     → AES-256-GCM envelope in <c>value_encrypted</c>; never
///                                returned by list APIs; re-resolved fresh at replay time
///                                (rotation is a feature, not a bug).
///   • everything else          → JSON-encoded value in <c>value_plain</c>; frozen into
///                                <c>workflow_run_variable</c> snapshot at run start so
///                                replay produces the same value the original run saw.
///
/// The (scope, valueType) pair is orthogonal: a <c>Team</c>-scoped variable can be any
/// valueType; a <c>Workflow</c>-scoped variable can be any valueType. The editor surfaces
/// the same UI shape for both — only the storage location and replay semantics differ.
/// </summary>
public enum VariableValueType
{
    String = 0,
    Number = 1,
    Boolean = 2,
    Object = 3,
    Array = 4,
    Secret = 5,
}

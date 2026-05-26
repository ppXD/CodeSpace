namespace CodeSpace.Messages.Constants;

/// <summary>
/// Permission name constants. v1 is empty — Admin role bypasses every check. Fine-grained
/// permissions land here when domain checks need them (e.g. "workflows:execute",
/// "repositories:bind", "credentials:rotate"). Partial class so per-domain permission sets
/// can live in their own files (Permissions.Workflow.cs, etc.).
/// </summary>
public static partial class Permissions
{
    // v1 intentionally empty. Add as IRequirePermission checks land.
}

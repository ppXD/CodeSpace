namespace CodeSpace.Messages.Constants;

/// <summary>
/// Role name constants (machine names — match role.name column). Use nameof() so the
/// constant name and string value cannot drift. Partial class lets future domain-specific
/// roles split into their own files (Roles.Workflow.cs, Roles.Billing.cs, etc.).
/// </summary>
public static partial class Roles
{
    /// <summary>Full system access; bypasses tenancy at the pipeline-behavior layer. Granted to the seeded system user.</summary>
    public const string Admin = nameof(Admin);
}

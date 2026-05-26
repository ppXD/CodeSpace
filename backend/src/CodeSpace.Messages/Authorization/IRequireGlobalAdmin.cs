namespace CodeSpace.Messages.Authorization;

/// <summary>
/// Marker — operation requires the Admin role (or the background seeded principal which
/// holds it). For system-level operations like managing global settings, cross-team admin
/// queries, user management. Pipeline behavior throws TenantAccessDeniedException for any
/// non-Admin caller.
/// </summary>
public interface IRequireGlobalAdmin
{
}

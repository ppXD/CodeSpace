namespace CodeSpace.Messages.Constants;

/// <summary>
/// Well-known UUIDs for system-seeded roles. As with <see cref="SystemUsers"/>, renaming
/// orphans DB rows — pinned by unit test.
/// </summary>
public static class SystemRoles
{
    /// <summary>The seeded Admin role row (is_system=true). Granted to <see cref="SystemUsers.SeederId"/> via migration 0004.</summary>
    public static readonly Guid AdminId = Guid.Parse("00000000-0000-0000-0000-000000000010");
}

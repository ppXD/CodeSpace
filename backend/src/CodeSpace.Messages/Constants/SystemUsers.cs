namespace CodeSpace.Messages.Constants;

/// <summary>
/// Well-known UUIDs for system-seeded users. These IDs are referenced from migration 0004
/// (the SQL INSERT) — renaming any constant orphans the DB row. Pinned by unit test.
/// </summary>
public static class SystemUsers
{
    /// <summary>The user under which background work (Hangfire workers, scheduled jobs, DbUp migrations) is attributed in audit columns. Holds the Admin role via migration 0004's role_user seed.</summary>
    public static readonly Guid SeederId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public const string SeederEmail = "system@codespace.local";
    public const string SeederName = "System";
}

using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Users;

public sealed record MeResponse
{
    /// <summary>
    /// INSTANCE-level capabilities this account holds — the <c>permission</c> tables, not a team role.
    /// Sent for the same reason the per-team list is: a client that has to guess whether an action
    /// exists will either hide something the person may do or offer something they may not.
    /// </summary>
    public required IReadOnlyList<string> Permissions { get; init; }

    public required Guid Id { get; init; }
    public required string Email { get; init; }
    public required string Name { get; init; }
    public string? AvatarUrl { get; init; }
    public required IReadOnlyList<MeTeam> Teams { get; init; }

    /// <summary>
    /// True when the user must rotate their password before they can do anything else.
    /// The SPA reacts by forcing the user to the /change-password screen.
    /// </summary>
    public required bool PasswordMustChange { get; init; }
}

public sealed record MeTeam
{
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }

    /// <summary>Personal = the user's solo space; Workspace = shared team. Drives sidebar grouping + disables "add member" / "delete team" actions on Personal.</summary>
    public required TeamKind Kind { get; init; }

    /// <summary>The caller's role in this team — Owner / Admin / Member / Viewer.</summary>
    public required TeamRole Role { get; init; }

    public required int MemberCount { get; init; }
    public required int RepositoryCount { get; init; }

    /// <summary>
    /// Active (non-soft-deleted) project count. Drives the sidebar's "Projects" nav-row
    /// badge in Phase 3.0 — that row previously surfaced repository count, which was
    /// the wrong concept now that the primary surface is Projects (each project holds
    /// its own repositories). Repositories without an explicit Project still land in
    /// the team's auto-seeded "default" project, so this count is always ≥ 1 for any
    /// team that's had a single bind happen.
    /// </summary>
    public required int ProjectCount { get; init; }

    /// <summary>
    /// Active (non-soft-deleted) workflow count — drives the sidebar's "Workflows" nav-row badge, the
    /// same uniform treatment as <see cref="ProjectCount"/>. Matches the team's workflow list query.
    /// </summary>
    public required int WorkflowCount { get; init; }

    /// <summary>
    /// Exactly what this caller may DO in this team, expanded from their role by
    /// <c>TeamPermissionMatrix</c> — the same table the server refuses on.
    ///
    /// <para>Sent so a client can hide what it must not offer WITHOUT reimplementing the rules. A
    /// frontend that derives ability from the role name keeps a second copy of the matrix, and the two
    /// drift the first time a permission moves tier: a button stays visible and answers 403.</para>
    /// </summary>
    public required IReadOnlyList<string> Permissions { get; init; }
}

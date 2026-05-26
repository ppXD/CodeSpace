namespace CodeSpace.Messages.Enums;

/// <summary>
/// Distinguishes a user's solo space from a multi-member workspace. The same Team table
/// backs both — every tenancy guard, RBAC check, provider-instance, credential, and
/// repository keys off team_id regardless of kind, which keeps the platform a single
/// generic primitive. Only the UI cares about the difference, to label Personal teams
/// distinctly and disable team-management operations (add member, delete) on them.
///
/// Invariants:
///   • Each non-system user has exactly one active Personal team (enforced by a partial
///     unique index in migration 0008).
///   • A Personal team's Owner is always the user it belongs to.
///   • Workspace teams have unrestricted membership; Personal teams are intentionally
///     single-member — any future member-add operation must refuse on Personal teams.
///
/// Mirroring GitHub's model: your personal account is "you"; organizations are separate
/// containers you can be added to. Same data shape, different label.
/// </summary>
public enum TeamKind
{
    /// <summary>Multi-member shared workspace. The default for any team the operator creates.</summary>
    Workspace = 0,

    /// <summary>Single-member space owned by the user. Auto-created on signup; never deleted.</summary>
    Personal = 1
}

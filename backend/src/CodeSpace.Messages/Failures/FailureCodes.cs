namespace CodeSpace.Messages.Failures;

/// <summary>
/// Every machine-readable failure code this API emits.
///
/// <para>These were 22 string literals typed into the exception filter at the point of mapping, with
/// no constant, no link to the exception they described, and no shared definition with the client
/// that branches on them. A code is a wire contract — the SPA routes a user to the password-rotation
/// form on one of these strings — so it belongs in one declared place where a rename is visible as
/// what it is: a breaking change.</para>
///
/// <para>Values are pinned by a unit test. Add one here before using it; the classifier asserts that
/// no failure ships a code this file does not declare.</para>
/// </summary>
public static class FailureCodes
{
    // ── Identity and access ────────────────────────────────────────────────────────
    public const string Unauthorized = "unauthorized";
    public const string InvalidCredentials = "invalid_credentials";
    public const string Forbidden = "forbidden";
    public const string PasswordRotationRequired = "password_rotation_required";
    public const string ActorIdentityRequired = "actor_identity_required";
    public const string ActorRepoPermissionDenied = "actor_repo_permission_denied";

    // ── Request shape and state ────────────────────────────────────────────────────
    public const string InvalidRequest = "invalid_request";
    public const string NotFound = "not_found";
    public const string DuplicateResource = "duplicate_resource";

    // ── Provider / OAuth ───────────────────────────────────────────────────────────
    public const string OAuthCallbackInvalid = "oauth_callback_invalid";
    public const string OAuthExchangeFailed = "oauth_exchange_failed";
    public const string OAuthInsufficientScope = "oauth_insufficient_scope";
    public const string ProviderUnauthorized = "provider_unauthorized";
    public const string ProviderError = "provider_error";
    public const string RateLimited = "rate_limited";

    // ── Workflows and runs ─────────────────────────────────────────────────────────
    public const string WorkflowDefinitionInvalid = "workflow_definition_invalid";
    public const string WorkspaceUnresolvable = "workspace_unresolvable";
    public const string RerunAlreadyInProgress = "rerun_already_in_progress";
    public const string RerunTargetInvalid = "rerun_target_invalid";
    public const string RerunBlockedUnsupportedNode = "rerun_blocked_unsupported_node";
    public const string RerunUpstreamNotReusable = "rerun_upstream_not_reusable";
    public const string PackImportFailed = "pack_import_failed";

    // ── Invitations ────────────────────────────────────────────────────────────────
    public const string InvitationNotUsable = "invitation_not_usable";
    public const string InvitationEmailMismatch = "invitation_email_mismatch";
    public const string InvitationRequiresSignIn = "invitation_requires_sign_in";
    public const string InvitationRoleExceedsGranter = "invitation_role_exceeds_granter";
    public const string PersonalTeamNotInvitable = "personal_team_not_invitable";
    public const string InvitationAlreadyPending = "invitation_already_pending";
    public const string AlreadyTeamMember = "already_team_member";

    // ── Membership ─────────────────────────────────────────────────────────────────
    public const string LastOwner = "last_owner";
    public const string RoleOutranksActor = "role_outranks_actor";
    public const string AccountDeactivated = "account_deactivated";
    public const string PasswordResetNotUsable = "password_reset_not_usable";

    /// <summary>The masked answer for anything unclassified. Never carries a message from the exception.</summary>
    public const string Internal = "internal_error";
}

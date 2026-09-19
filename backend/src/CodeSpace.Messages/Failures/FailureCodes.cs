using System.Collections.Generic;
using System.Linq;
using System.Reflection;

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
    public const string AgentRunOwnershipLost = "agent-run-ownership-lost";
    public const string AgentRunLaunchAcknowledgementLost = "agent-run-launch-acknowledgement-lost";
    public const string AgentAuthorityDenied = "agent.authority_denied";
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
    public const string StorageCredentialInvalid = "storage_credential_invalid";
    public const string StorageCredentialConflict = "storage_credential_conflict";
    public const string StorageProfileInvalid = "storage_profile_invalid";
    public const string StorageProfileConflict = "storage_profile_conflict";
    public const string StorageRouteInvalid = "storage_route_invalid";
    public const string StorageRouteConflict = "storage_route_conflict";
    public const string StorageDefaultInvalid = "storage_default_invalid";
    public const string StorageDefaultConflict = "storage_default_conflict";
    public const string ArtifactContentUnavailable = "artifact_content_unavailable";
    public const string ArtifactStorageDestinationUnavailable = "artifact_storage_destination_unavailable";

    // ── Provider / OAuth ───────────────────────────────────────────────────────────
    public const string OAuthCallbackInvalid = "oauth_callback_invalid";
    public const string OAuthExchangeFailed = "oauth_exchange_failed";
    public const string OAuthInsufficientScope = "oauth_insufficient_scope";
    public const string ProviderUnauthorized = "provider_unauthorized";
    public const string ProviderError = "provider_error";
    public const string RateLimited = "rate_limited";

    // ── Workflows and runs ─────────────────────────────────────────────────────────
    public const string WorkflowDefinitionInvalid = "workflow_definition_invalid";
    public const string TaskRouteConfirmationRequired = "task_route_confirmation_required";
    public const string TaskRouteSnapshotMismatch = "task_route_snapshot_mismatch";
    public const string WorkspaceUnresolvable = "workspace_unresolvable";
    public const string RerunAlreadyInProgress = "rerun_already_in_progress";
    public const string RerunTargetInvalid = "rerun_target_invalid";
    public const string RerunBlockedUnsupportedNode = "rerun_blocked_unsupported_node";
    public const string RerunUpstreamNotReusable = "rerun_upstream_not_reusable";
    public const string PackImportFailed = "pack_import_failed";

    /// <summary>A settled cell's outputs were redacted for persistence and their encrypted recovery payload is not readable, so the originals exist nowhere. Remedy: re-run the workflow from that node — a resume can only ever offer the redaction placeholder.</summary>
    public const string WorkflowOutputsUnrecoverable = "workflow_outputs_unrecoverable";

    // ── Invitations ────────────────────────────────────────────────────────────────
    public const string InvitationNotUsable = "invitation_not_usable";
    public const string InvitationEmailMismatch = "invitation_email_mismatch";
    public const string InvitationRequiresSignIn = "invitation_requires_sign_in";
    public const string InvitationRoleExceedsGranter = "invitation_role_exceeds_granter";
    public const string PersonalTeamNotInvitable = "personal_team_not_invitable";

    /// <summary>
    /// The provider refused because the account's PLAN does not include the feature, not because the
    /// credential lacks a scope. Distinct from a permission failure because re-issuing the token cannot
    /// help — GitLab group webhooks are Premium, and a Free instance answers the same 403 a
    /// wrongly-scoped token would.
    /// </summary>
    public const string ProviderPlanRequired = "provider_plan_required";
    public const string InvitationAlreadyPending = "invitation_already_pending";
    public const string AlreadyTeamMember = "already_team_member";

    // ── Membership ─────────────────────────────────────────────────────────────────
    public const string LastOwner = "last_owner";
    public const string RoleOutranksActor = "role_outranks_actor";
    public const string AccountDeactivated = "account_deactivated";
    public const string PasswordResetNotUsable = "password_reset_not_usable";

    /// <summary>The masked answer for anything unclassified. Never carries a message from the exception.</summary>
    public const string Internal = "internal_error";

    /// <summary>W-hard: the run's cost cap is spent — the budget ledger refused the next model call. Remedy: a bigger cap or a narrower goal, never a retry.</summary>
    public const string RunBudgetExhausted = "run_budget_exhausted";

    /// <summary>D1: the run declares a cost cap but the model it would spend on has no price, so the cap is unenforceable. Remedy: price the model in the model manager, or remove the cap — a retry of the identical call can never succeed.</summary>
    public const string ModelPriceRequired = "model_price_required";

    /// <summary>P15: a model call reached the budget guard with no launch scope wired — a programming defect, never an operator-facing failure. Remedy: thread the plane's budget ledger + cap, or mark it explicitly Unbudgeted with a reason.</summary>
    public const string UnscopedModelCall = "unscoped_model_call";

    /// <summary>A harness capture stream skipped ordinals, so the records between them can never be folded. Remedy: recover or record the gap — a reduction that advanced over it would store a partial prefix as a whole one.</summary>
    public const string HarnessReductionGap = "harness_reduction_gap";

    /// <summary>A captured native record does not hold together — its payload binding, digest, redaction claim or projection attribution contradicts itself — so it cannot be reduced. Remedy: fix the producer; the record is retained either way.</summary>
    public const string HarnessRecordUnreadable = "harness_record_unreadable";

    /// <summary>A command observation is incomplete for a full-output consumer. Recover the observation without replaying the command's side effects.</summary>
    public const string SandboxOutputIncomplete = "sandbox_output_incomplete";
    public const string NativeLaunchUnavailable = "native_launch_unavailable";

    /// <summary>The invocation itself is unexecutable: one of its argv/environment strings is past the kernel's per-string ceiling, so <c>execve</c> would refuse it with E2BIG. Distinct from <see cref="NativeLaunchUnavailable"/>, which is about the SLOT and can be retried elsewhere — every host refuses these same bytes, so a retry is N identical refusals. Remedy: shorten the text, or hand it to the agent as a file.</summary>
    public const string SandboxArgumentTooLong = "sandbox_argument_too_long";

    /// <summary>This host cannot reserve a filtered-egress run's own /30 subnet, so the run is refused rather than handed one nothing reserved. Remedy: make the reservation directory under the agent-run spool root writable by the worker — a retry on the same host cannot help.</summary>
    public const string SandboxEgressReservationUnavailable = "sandbox_egress_reservation_unavailable";

    /// <summary>A run's model credential could not be brokered on a deployment that requires confinement, so the run is refused rather than handed the tenant's long-lived provider key. Remedy: make the worker able to bind a broker listener, use a harness that honours a base-URL override, or store an upstream endpoint on the credential — a retry on the same host cannot help.</summary>
    public const string ModelCredentialBrokerUnavailable = "model_credential_broker_unavailable";

    /// <summary>The worker holding this run's brokered model-credential lease went away, so the detached agent's model access ended with it and its attempt can make no further model call. Remedy: a retry, which starts a fresh attempt on a live worker — nothing about the run's own inputs is wrong, and nothing on the provider's side is down.</summary>
    public const string ModelCredentialLeaseLost = "model_credential_lease_lost";

    /// <summary>
    /// The codes that, worn as an agent attempt's <c>AgentRunResult.ExitReason</c>, say the attempt died on OUR
    /// INFRASTRUCTURE — the worker went away, the broker could not bind — rather than on anything the agent did or
    /// the model answered. A post-hoc grader that meets one of these is grading an attempt whose check never ran and
    /// never could have, so no further agent pass can change its verdict.
    ///
    /// <para>Deliberately a SET of exit reasons, not of failure codes in general: <see cref="All"/> declares every
    /// code this API can emit, and most of them (an invalid request, a missing rubric, a spent budget) are genuine
    /// answers about the work. Membership here is the narrower claim that the run never got to be about the work at
    /// all. Pinned member-by-member by a unit test — adding or removing one changes what a post-hoc grade can be.</para>
    ///
    /// <para><b>Membership settles the CLASSIFICATION, never the REMEDY</b>, and the two members already differ on
    /// the second. <see cref="ModelCredentialLeaseLost"/> is a worker that went away mid-run: the identical attempt
    /// on a live worker simply succeeds, so its repair is a retry and nothing else. <see cref="ModelCredentialBrokerUnavailable"/>
    /// is a worker that could not broker at all on a deployment mandating confinement — a retry helps only if it
    /// lands somewhere that CAN broker, and if the deployment itself is misconfigured no number of attempts will
    /// (its own remedy line above says as much). Both are equally "not about the work", which is all this set
    /// claims; what to DO about each is the prompt renderer's question, and
    /// <c>LlmSupervisorDecider.EndedByDeploymentSteer</c> answers it per exit reason rather than per class.</para>
    ///
    /// <para>The gateway FORMAT fault (<c>AgentRetryCauses.GatewayFormatFault</c>) belongs to this family and is
    /// deliberately ABSENT: it is recognised by matching the harness's error TEXT, not by any exit reason, and it
    /// joins if it ever earns one — matching prose here would make this set's answer depend on wording, which is the
    /// thing it exists to replace.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> InfraExitReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        ModelCredentialLeaseLost,
        ModelCredentialBrokerUnavailable,
    };

    /// <summary>
    /// Every code declared above, computed by reflection so it can never drift from the constants themselves. For a
    /// caller that must treat ANY coded exit as this codebase's own diagnosis of what went wrong — never a downstream
    /// heuristic's guess — membership here is the check: see <c>RealModelRunClassifier.IsGatewayInfra</c>, which
    /// reserves every code in this set as "our fault, not an infra skip" even when the failure's own message happens
    /// to contain gateway-looking vocabulary.
    /// </summary>
    public static readonly IReadOnlySet<string> All = typeof(FailureCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToHashSet(StringComparer.Ordinal);
}

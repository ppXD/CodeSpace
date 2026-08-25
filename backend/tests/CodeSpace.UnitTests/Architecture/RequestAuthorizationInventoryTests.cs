using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using MediatR;
using Shouldly;

namespace CodeSpace.UnitTests.Architecture;

/// <summary>
/// Fail-closed floor for the authorization pipeline: every MediatR request in
/// <c>CodeSpace.Messages</c> declares an <c>IRequire*</c> marker, or is named here with the reason it
/// cannot.
///
/// <para>Why pin it: the marker is what selects a request into an authorization behavior. A request
/// without one is not "authorized by default" — it is not authorized at all, and it looks exactly
/// like every other request in the diff. Today the unmarked set is a deliberate, auditable 21; this
/// test makes the 22nd a build failure rather than a discovery.</para>
///
/// <para>The two allow-lists are kept SEPARATE on purpose. Adding a sweep is routine; adding an
/// anonymous entry opens an unauthenticated surface and deserves a reviewer who knows that is what
/// they are approving.</para>
/// </summary>
[Trait("Category", "Unit")]
public class RequestAuthorizationInventoryTests
{
    /// <summary>
    /// Reached without a CodeSpace principal because the caller proves itself another way. Every entry
    /// must name that proof — "it's a public endpoint" is not one.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AnonymousByDesign = new Dictionary<string, string>
    {
        ["SignInCommand"] = "the call that mints the JWT; proof is the password.",
        ["ReceiveWebhookCommand"] = "provider-signed inbound; proof is the HMAC verified against the per-webhook secret.",
        ["ReceiveConnectionWebhookCommand"] = "the same inbound, from a group/organization hook; proof is the HMAC verified against THAT hook's own secret, never a repository's.",
        ["ResumeWorkflowCallbackCommand"] = "proof is the single-use high-entropy callback token in the URL.",
        ["CompleteCredentialOAuthCommand"] = "the provider redirect carries no JWT; proof is the one-time OAuthPendingState row.",
        ["AcceptInvitationCommand"] = "the invitee has no account yet; proof is the single-use invitation token in the route.",
        ["PreviewInvitationQuery"] = "same token, read-only, and it answers nothing at all unless the token checks out.",
        ["ResetPasswordCommand"] = "someone who cannot sign in is exactly who needs this; proof is the single-use reset token in the route.",
    };

    /// <summary>
    /// Recurring sweeps dispatched by Hangfire with no HTTP context. They run as the seeded system
    /// principal (which holds Admin), so a marker would be satisfied trivially and would only mislead.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> SystemSweepByDesign = new Dictionary<string, string>
    {
        ["CleanupExpiredOAuthStatesCommand"] = "sweep",
        ["ExpireStaleDecisionsCommand"] = "sweep",
        ["ExpireStaleSupervisorDecisionsCommand"] = "sweep",
        ["ExpireStaleToolApprovalsCommand"] = "sweep",
        ["ExpireStaleToolCallsCommand"] = "sweep",
        ["FireDueScheduleTriggersCommand"] = "sweep",
        ["ProbeStaleModelAvailabilityCommand"] = "sweep",
        ["ProbeUnknownModelCapabilitiesCommand"] = "sweep",
        ["ProjectWorkflowRunModelCallsCommand"] = "projects started and terminal workflow-run interaction evidence; it is dispatched only by the bounded system recurring job.",
        ["ProjectWorkflowRunToolCallsCommand"] = "projects governed side-effect ledger facts into the observation-only Workflow Run tool-call plane; it is dispatched only by the bounded system recurring job.",
        ["MaterializeWorkflowRunModelCallBodiesCommand"] = "materializes already-declared telemetry bodies in bounded lease/fence batches; it changes no Workflow Run outcome.",
        ["ReapAgentRunSpoolsCommand"] = "sweep",
        ["ReapUnreferencedArtifactsCommand"] = "collects artifacts that a producer declared for retention and that no reference site points at, in bounded lease/fence batches; it is dispatched only by the system recurring job, acts for no user, and can never reach an artifact no producer declared.",
        ["ReconcileAgentRunLogCapturesCommand"] = "reconciles exact durable AgentRun log-capture health in bounded lease/fence batches; it is dispatched only by the system recurring job and never acts for a user or changes an AgentRun outcome.",
        ["ReconcileStuckAgentRunsCommand"] = "sweep",
        ["ReconcileStuckRunsCommand"] = "sweep",
        ["DistillLessonsCommand"] = "sweep",
        ["ReconcileStuckWebhookRegistrationsCommand"] = "sweep",
        ["SweepBudgetSettlementCommand"] = "sweep",
        ["SweepCompletionShadowCommand"] = "sweep",
        ["SweepStaleAgentWorkspacesCommand"] = "sweep",
        ["TierStaleModelCapabilitiesCommand"] = "sweep",
        ["WarnUnrotatedBootstrapPasswordsCommand"] = "sweep",
    };

    [Fact]
    public void Every_request_declares_an_authorization_marker_unless_allow_listed()
    {
        var offenders = RequestTypes()
            .Where(t => !HasAuthorizationMarker(t))
            .Where(t => !AnonymousByDesign.ContainsKey(t.Name) && !SystemSweepByDesign.ContainsKey(t.Name))
            .Select(t => t.FullName!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "these requests reach a handler with no authorization behavior selecting them. Add the marker that fits " +
            "(IRequireTeamPermission for a team write, IRequireTeamMembership for a team read, IRequireAuthenticatedUser " +
            "for a self/catalog call) — or, if it genuinely runs without a principal, add it to AnonymousByDesign or " +
            "SystemSweepByDesign with the reason:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_allow_lists_do_not_rot()
    {
        // A name that no longer exists, or that has since gained a marker, must be removed — otherwise the
        // list quietly grows into a place where a real gap can hide.
        foreach (var name in AnonymousByDesign.Keys.Concat(SystemSweepByDesign.Keys))
        {
            var type = RequestTypes().SingleOrDefault(t => t.Name == name);

            type.ShouldNotBeNull($"allow-listed request '{name}' no longer exists — remove it");
            HasAuthorizationMarker(type!).ShouldBeFalse($"allow-listed request '{name}' now declares an authorization marker — remove it from the allow-list");
        }
    }

    [Fact]
    public void The_two_allow_lists_do_not_overlap()
    {
        var both = AnonymousByDesign.Keys.Intersect(SystemSweepByDesign.Keys).ToList();

        both.ShouldBeEmpty("a request is either externally authenticated or a system sweep, never both:\n  " + string.Join("\n  ", both));
    }

    [Fact]
    public void Every_declared_team_permission_is_one_the_matrix_knows()
    {
        // RequiredPermission is an instance property returning a constant, so it can only be read off an
        // instance. These records have required members, so allocate uninitialized rather than construct —
        // the getter touches no state.
        var adopters = RequestTypes().Where(t => typeof(IRequireTeamPermission).IsAssignableFrom(t)).ToList();

        adopters.ShouldNotBeEmpty("no request implements IRequireTeamPermission — the checks below would pass vacuously");

        var unknown = new List<string>();

        foreach (var type in adopters)
        {
            var permission = ((IRequireTeamPermission)RuntimeHelpers.GetUninitializedObject(type)).RequiredPermission;

            if (!TeamPermissionMatrix.All.Contains(permission)) unknown.Add($"{type.Name} → '{permission}'");
        }

        unknown.ShouldBeEmpty("these requests declare a permission the matrix has no row for, so every call throws at runtime:\n  " + string.Join("\n  ", unknown));
    }

    [Fact]
    public void A_team_permission_request_is_also_team_scoped()
    {
        // IRequireTeamPermission extends IRequireTeamMembership so this holds structurally; asserted so a
        // future refactor that unhooks the two surfaces here rather than as a tenancy hole.
        foreach (var type in RequestTypes().Where(t => typeof(IRequireTeamPermission).IsAssignableFrom(t)))
            typeof(IRequireTeamMembership).IsAssignableFrom(type).ShouldBeTrue($"{type.Name} declares a team permission but is not team-scoped");
    }

    private static IEnumerable<Type> RequestTypes()
    {
        // Keyed on MediatR's root marker, NOT ICommand/IQuery: 23 requests derive from IRequest<T>
        // directly, and keying on the house wrappers would skip every one of them silently.
        var types = typeof(TeamPermissions).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => typeof(IBaseRequest).IsAssignableFrom(t))
            .ToList();

        types.ShouldNotBeEmpty("the reflection scan found no requests — every check in this class would pass vacuously");

        return types;
    }

    private static bool HasAuthorizationMarker(Type type) =>
        typeof(IRequireAuthenticatedUser).IsAssignableFrom(type)
        || typeof(IRequireGlobalAdmin).IsAssignableFrom(type)
        || typeof(IRequireGlobalPermission).IsAssignableFrom(type)
        || typeof(IRequireTeamMembership).IsAssignableFrom(type)
        || typeof(IRequireRepositoryAccess).IsAssignableFrom(type)
        || typeof(IRequireCredentialAccess).IsAssignableFrom(type);
}

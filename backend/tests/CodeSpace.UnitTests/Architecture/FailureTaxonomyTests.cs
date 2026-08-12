using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CodeSpace.Core.Failures;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.UnitTests.Architecture;

/// <summary>
/// Keeps the failure taxonomy honest: codes stay stable, every declared code is reachable, and the
/// exceptions that have not adopted it yet stay counted instead of quietly becoming permanent.
/// </summary>
[Trait("Category", "Unit")]
public class FailureTaxonomyTests
{
    /// <summary>
    /// Domain exceptions that still signal nothing about themselves, so a caller learns only that
    /// SOMETHING went wrong — they reach the API as a masked 500 and a job as an undifferentiated
    /// error. Each is a decision waiting to be made, not an exemption: adopting one means deleting
    /// its line.
    /// </summary>
    private static readonly IReadOnlySet<string> AwaitingAdoption = new HashSet<string>
    {
        "AgentDefinitionResolutionException", "AgentRunAdmissionException", "AgentRunTransitionException",
        "LlmApiException", "MissingProjectRefException", "MissingRequiredInputException",
        "ModelCredentialResolutionException", "NodeFailureException", "ReleaseTamperedException",
        "SubworkflowStartException", "SupervisorAgentAccessException",
        "SupervisorDecisionTransitionException", "SupervisorModelAccessException",
        "SupervisorRepoAccessException", "ToolCallLedgerTransitionException", "WorkflowSecretLeakException",
    };

    /// <summary>
    /// Throws that are a jump, not a fault. Both are caught by the code that threw them — a suspended
    /// run is the SUCCESS path for a wait node, and a stalled sandbox is selected as a status sixty
    /// lines from where it is raised. Classifying them would invite someone to render a parked run to
    /// a user as an error.
    /// </summary>
    private static readonly IReadOnlySet<string> ControlFlowNotFailure = new HashSet<string>
    {
        "RunSuspendedException",
        "AgentStalledException",
    };

    [Fact]
    public void The_wire_codes_are_pinned()
    {
        // A client routes a signed-in user to the password-rotation form on one of these strings, and
        // opens the identity-link modal on another. They are API, not identifiers.
        FailureCodes.Forbidden.ShouldBe("forbidden");
        FailureCodes.Unauthorized.ShouldBe("unauthorized");
        FailureCodes.InvalidCredentials.ShouldBe("invalid_credentials");
        FailureCodes.PasswordRotationRequired.ShouldBe("password_rotation_required");
        FailureCodes.ActorIdentityRequired.ShouldBe("actor_identity_required");
        FailureCodes.ActorRepoPermissionDenied.ShouldBe("actor_repo_permission_denied");
        FailureCodes.InvalidRequest.ShouldBe("invalid_request");
        FailureCodes.NotFound.ShouldBe("not_found");
        FailureCodes.DuplicateResource.ShouldBe("duplicate_resource");
        FailureCodes.OAuthCallbackInvalid.ShouldBe("oauth_callback_invalid");
        FailureCodes.OAuthExchangeFailed.ShouldBe("oauth_exchange_failed");
        FailureCodes.OAuthInsufficientScope.ShouldBe("oauth_insufficient_scope");
        FailureCodes.ProviderUnauthorized.ShouldBe("provider_unauthorized");
        FailureCodes.ProviderError.ShouldBe("provider_error");
        FailureCodes.RateLimited.ShouldBe("rate_limited");
        FailureCodes.WorkflowDefinitionInvalid.ShouldBe("workflow_definition_invalid");
        FailureCodes.WorkspaceUnresolvable.ShouldBe("workspace_unresolvable");
        FailureCodes.RerunAlreadyInProgress.ShouldBe("rerun_already_in_progress");
        FailureCodes.RerunTargetInvalid.ShouldBe("rerun_target_invalid");
        FailureCodes.RerunBlockedUnsupportedNode.ShouldBe("rerun_blocked_unsupported_node");
        FailureCodes.RerunUpstreamNotReusable.ShouldBe("rerun_upstream_not_reusable");
        FailureCodes.PackImportFailed.ShouldBe("pack_import_failed");
        FailureCodes.Internal.ShouldBe("internal_error");
    }

    [Fact]
    public void Every_failure_declares_a_code_this_assembly_knows()
    {
        // A code typed as a literal is a code no client can be told about and no test can pin.
        var declared = DeclaredCodes();

        var strangers = FailureTypes()
            .Select(t => new { t.Name, ((IFailure)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t)).Code })
            .Where(x => !declared.Contains(x.Code))
            .Select(x => $"{x.Name} → '{x.Code}'")
            .ToList();

        strangers.ShouldBeEmpty("these failures ship a code that is not declared in FailureCodes:\n  " + string.Join("\n  ", strangers));
    }

    [Fact]
    public void Every_declared_code_is_reachable()
    {
        // Every code the classifier can emit, from either an IFailure or one of its BCL arms.
        var emitted = FailureTypes()
            .Select(t => ((IFailure)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t)).Code)
            // The classifier's BCL arms, plus the branch a probed instance cannot reveal: a zeroed
            // ProviderApiException reports provider_error, and only a real 401/403 from a provider
            // produces provider_unauthorized. A probe sees one side of a conditional Code, never both.
            .Concat(new[] { FailureCodes.Unauthorized, FailureCodes.NotFound, FailureCodes.DuplicateResource, FailureCodes.InvalidRequest, FailureCodes.Internal, FailureCodes.ProviderUnauthorized })
            .ToHashSet(StringComparer.Ordinal);

        var unreachable = DeclaredCodes().Where(c => !emitted.Contains(c)).OrderBy(c => c, StringComparer.Ordinal).ToList();

        unreachable.ShouldBeEmpty("these codes are declared but nothing can produce them, so a client branching on one waits forever:\n  " + string.Join("\n  ", unreachable));
    }

    [Fact]
    public void Every_domain_exception_declares_its_meaning_or_is_counted_as_not_yet_doing_so()
    {
        var silent = DomainExceptionTypes()
            .Where(t => !typeof(IFailure).IsAssignableFrom(t))
            .Where(t => !AwaitingAdoption.Contains(t.Name) && !ControlFlowNotFailure.Contains(t.Name))
            .Select(t => t.FullName!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        silent.ShouldBeEmpty(
            "these exceptions say nothing about what they mean, so they reach a caller as a masked 500 and a job as an " +
            "undifferentiated error. Implement IFailure with the kind and code that fit — or add the name to " +
            "AwaitingAdoption to record that it is known and unfinished:\n  " + string.Join("\n  ", silent));
    }

    [Fact]
    public void The_adoption_list_does_not_rot()
    {
        foreach (var name in AwaitingAdoption.Concat(ControlFlowNotFailure))
        {
            var type = DomainExceptionTypes().SingleOrDefault(t => t.Name == name);

            type.ShouldNotBeNull($"'{name}' no longer exists — remove it from AwaitingAdoption");
            typeof(IFailure).IsAssignableFrom(type!).ShouldBeFalse($"'{name}' now declares its meaning — remove it from AwaitingAdoption");
        }
    }

    [Fact]
    public void An_internal_failure_never_carries_a_message_from_the_exception()
    {
        // The one rule that cannot be got wrong: an invariant breach must not narrate itself to a
        // caller. The message is a constant so it cannot vary by input, either.
        var classification = FailureClassifier.Classify(new Exception("connection string: Host=prod;Password=hunter2"));

        classification.Kind.ShouldBe(FailureKind.Internal);
        classification.ClientMessage.ShouldBe(FailureClassifier.MaskedMessage);
        classification.ClientMessage.ShouldNotContain("hunter2");
    }

    [Fact]
    public void A_cancelled_operation_is_not_reported_as_an_internal_fault()
    {
        // A caller hanging up is not a failure of ours. It classifies as Internal only because nothing
        // claims it — pinned so that stays a deliberate position rather than an accident, since it is
        // what decides whether a closed browser tab pages someone.
        FailureClassifier.Classify(new OperationCanceledException()).Kind.ShouldBe(FailureKind.Internal);
    }

    private static IReadOnlySet<string> DeclaredCodes()
    {
        var codes = typeof(FailureCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        codes.ShouldNotBeEmpty("the reflection scan found no codes — every check here would pass vacuously");

        return codes;
    }

    private static IEnumerable<Type> FailureTypes() =>
        DomainExceptionTypes().Where(t => typeof(IFailure).IsAssignableFrom(t));

    /// <summary>
    /// Exceptions this codebase defines, across both assemblies MediatR and the API load. Nested and
    /// private types are included deliberately — a private exception that escapes its file is exactly
    /// the kind that reaches a caller unclassified.
    /// </summary>
    private static IEnumerable<Type> DomainExceptionTypes()
    {
        var assemblies = new[] { typeof(FailureCodes).Assembly, typeof(FailureClassifier).Assembly };

        var types = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsClass && !t.IsAbstract && typeof(Exception).IsAssignableFrom(t))
            .ToList();

        types.ShouldNotBeEmpty("the reflection scan found no exceptions — every check here would pass vacuously");

        return types;
    }
}

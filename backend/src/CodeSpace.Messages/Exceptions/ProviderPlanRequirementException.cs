using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// The provider understood the call and refused it because the account's plan does not include the
/// feature. GitLab group webhooks are the case this exists for: the endpoint is Premium, and a Free
/// instance answers 403 (or 404 on some versions, which is the same refusal wearing a different
/// number).
///
/// <para><see cref="FailureKind.PreconditionRequired"/> rather than Forbidden, because the two mean
/// opposite things to whoever reads them. Forbidden says "retrying as this principal cannot help",
/// which would send an operator to re-scope a token that is already correct. This says something
/// nameable must happen first — upgrade the group, or leave the connection on per-repository scope —
/// and then this exact call works.</para>
///
/// <para>The message names the plan; the provider's own status and body are carried separately in
/// the attempt row's diagnostic, so the operator reads the refusal in the provider's words as well
/// as ours. Neither replaces the other: ours says what to DO, theirs is the evidence.</para>
/// </summary>
public sealed class ProviderPlanRequirementException : Exception, IFailure, IUpstreamStatus
{
    public FailureKind Kind => FailureKind.PreconditionRequired;

    public string Code => FailureCodes.ProviderPlanRequired;

    public IReadOnlyDictionary<string, object?>? Details => new Dictionary<string, object?>
    {
        ["provider"] = ProviderKind.ToString(),
        ["feature"] = Feature,
        ["requiredPlan"] = RequiredPlan,
        ["providerStatus"] = StatusCode
    };

    int IUpstreamStatus.UpstreamStatus => StatusCode;

    public ProviderPlanRequirementException(ProviderKind providerKind, string feature, string requiredPlan, int statusCode, string remedy, Exception inner)
        : base(BuildMessage(providerKind, feature, requiredPlan, statusCode, remedy), inner)
    {
        ProviderKind = providerKind;
        Feature = feature;
        RequiredPlan = requiredPlan;
        StatusCode = statusCode;
    }

    public ProviderKind ProviderKind { get; }

    /// <summary>What was being attempted, in the provider's own vocabulary ("group webhooks").</summary>
    public string Feature { get; } = string.Empty;

    /// <summary>The plan tier the provider documents as the floor for <see cref="Feature"/> ("Premium").</summary>
    public string RequiredPlan { get; } = string.Empty;

    /// <summary>Status the provider refused with. 403 and 404 are both refusals here; carrying it keeps them distinguishable.</summary>
    public int StatusCode { get; }

    private static string BuildMessage(ProviderKind kind, string feature, string requiredPlan, int statusCode, string remedy)
    {
        return $"{kind} {feature} require the {requiredPlan} plan — the provider answered HTTP {statusCode}. {remedy}";
    }
}

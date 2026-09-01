namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>A local control-flow jump from a nested provider adapter to the adopter's typed refusal boundary.</summary>
internal sealed class LegacyProviderRejectedException : Exception
{
    public LegacyProviderRejectedException() : base("The storage provider rejected a legacy-adoption operation.") { }
}

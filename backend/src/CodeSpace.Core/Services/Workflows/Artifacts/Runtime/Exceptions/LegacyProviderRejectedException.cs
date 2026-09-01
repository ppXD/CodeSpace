namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>A local control-flow jump from a nested provider adapter to the adopter's typed refusal boundary.</summary>
internal sealed class LegacyProviderRejectedException : Exception
{
    public LegacyProviderRejectedException() : base("The storage provider rejected a legacy-adoption operation.") { }
}

/// <summary>A local jump that stops all further work on a lease whose timed-out operation may still be running.</summary>
internal sealed class LegacyProviderLeasePoisonedException : Exception
{
    public LegacyProviderLeasePoisonedException() : base("A timed-out legacy-adoption provider lease cannot accept another operation.") { }
}

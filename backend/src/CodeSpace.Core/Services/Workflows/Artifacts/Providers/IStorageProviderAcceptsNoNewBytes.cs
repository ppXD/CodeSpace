namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers;

/// <summary>
/// A provider that never accepts new bytes, whatever state its destination is in. Its objects were placed by some
/// earlier process and it exists to be READ — surveyed, verified, eventually adopted — never written to.
///
/// <para>A sibling marker rather than a widening of <see cref="IStorageProviderModule"/> (Rule 7): taking writes is
/// the ordinary case, and a provider that takes them must not have to say so.</para>
///
/// <para>What it is for: every other write gate in the plane asks a destination whether it is taking bytes TODAY —
/// <c>StorageRouteService.ProveDestinationWritableAsync</c> writes and discards one real object, the health sweep
/// re-probes. Those questions have a temporary answer, so their refusals read as "try again once the destination is
/// fixed". This one has no such answer: a routing decision made against such a provider can never come good, and
/// without the marker its refusal would arrive at the first artifact write, at runtime, where no operator is
/// standing. Route binding therefore refuses it by declaration, before the probe is ever asked.</para>
/// </summary>
public interface IStorageProviderAcceptsNoNewBytes;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Routing;

/// <summary>
/// Optional sibling capability of <see cref="IRoutedDataClass"/> (Rule 7 / ISP — never a widening of it): this data
/// class has a durable home that is NOT the routing plane, so a team that has not cut it over is a lawful state rather
/// than a refusal. It carries no members: the declaration itself is the whole of the policy, and there is nothing a
/// class could configure about it that the consumer does not already own.
///
/// <para><b>What implementing it changes.</b> Exactly the two pre-cutover outcomes, and only those two.
/// <c>Missing</c> (no route at all — the shipped state of every team that never configured one) and
/// <c>RouteNotActivated</c> (a route created and never activated) resolve to
/// <c>RoutedDestination.Local</c> instead of a typed refusal. Every other outcome — a route an operator stopped, a
/// profile that is not Active, a self-contradictory pointer, routing state that could not be read — is unchanged and
/// still fails closed, because degrading THOSE to a local home would tell the operator their storage choice is in
/// effect while it is not.</para>
///
/// <para><b>Not implementing it is the default and the safe one.</b> A class whose only home is the routing plane —
/// Agent Run log capture is the shipped example — must refuse until it is cut over, or capture is silently dropped on
/// the floor. The absence of this interface is therefore read as "refuse", never as "unspecified".</para>
///
/// <para><b>The obligation the declaration does not carry on its own.</b> Declaring it does not create a local
/// backend. The consumer must already have somewhere to put the bytes and must handle
/// <c>RoutedDestination.Local</c>. Nothing here can check that, so a consumer that cannot honour the arm should raise
/// rather than absorb it — <c>AgentRunLogStorageResolver</c> throws on it for exactly that reason.</para>
/// </summary>
public interface IRoutedDataClassLocalFallback;

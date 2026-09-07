using Autofac;
using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.Nodes;

/// <summary>Runs one node invocation with its own scoped dependencies while retaining the owning execution scope's identity and services.</summary>
public interface INodeInvocationExecutor
{
    Task<NodeResult> ExecuteAsync(NodeInvocation invocation, CancellationToken cancellationToken);
}

public sealed record NodeInvocation(string TypeKey, NodeRunContext Context);

/// <summary>A catalog may outlive or serve concurrent calls. Its runtimes must not share a DbContext across those calls.</summary>
public sealed class NodeInvocationExecutor(ILifetimeScope owner) : INodeInvocationExecutor, IScopedDependency
{
    public async Task<NodeResult> ExecuteAsync(NodeInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A child of the owning scope preserves caller-bound actor/team/delegation services. A new root scope
        // would discard those bindings. Runtime lookup remains generic for every registered plugin node.
        await using var scope = owner.BeginLifetimeScope();
        return await scope.Resolve<INodeRegistry>().Resolve(invocation.TypeKey).RunAsync(invocation.Context, cancellationToken).ConfigureAwait(false);
    }
}

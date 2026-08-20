namespace CodeSpace.Core.Services.Workflows.Artifacts.Routing.DataClasses;

/// <summary>
/// The main artifact plane: node outputs, patches, manifests, transcripts and model-call bodies large enough to be
/// offloaded. The key is taken from the resolver that reads it, so the declaration cannot drift from the consumer.
///
/// <para>It declares <see cref="IRoutedDataClassLocalFallback"/> because this plane keeps a local blob backend: a team
/// that has no route, or has one it created and never activated, writes there verbatim. That is the whole of the
/// difference between this class's destination policy and the Agent Run log class's.</para>
/// </summary>
public sealed class WorkflowArtifactDataClass : IRoutedDataClass, IRoutedDataClassLocalFallback
{
    public string TypeKey => WorkflowArtifactDestinationResolver.DataClassTypeKey;

    public string DisplayName => "Workflow artifacts";
}

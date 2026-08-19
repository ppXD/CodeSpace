namespace CodeSpace.Core.Services.Workflows.Artifacts.Routing.DataClasses;

/// <summary>
/// The main artifact plane: node outputs, patches, manifests, transcripts and model-call bodies large enough to be
/// offloaded. The key is taken from the resolver that reads it, so the declaration cannot drift from the consumer.
/// </summary>
public sealed class WorkflowArtifactDataClass : IRoutedDataClass
{
    public string TypeKey => WorkflowArtifactDestinationResolver.DataClassTypeKey;

    public string DisplayName => "Workflow artifacts";
}

using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Workflows.RunSources;

/// <summary>The matched server row, including a DB-rotated revision, bound to the exact workflow version admitted.</summary>
public static class ActivationAuthoritySnapshot
{
    public static string Serialize(WorkflowActivation activation) => JsonSerializer.Serialize(new
    {
        id = activation.Id,
        typeKey = activation.TypeKey,
        config = JsonDocument.Parse(activation.ConfigJson).RootElement,
        enabled = activation.Enabled,
        authorityRevision = activation.AuthorityRevision,
        publisherId = Publisher(activation),
        workflowVersion = activation.Workflow.LatestVersion,
    });

    public static Guid Publisher(WorkflowActivation activation) => activation.LastModifiedBy != Guid.Empty ? activation.LastModifiedBy : activation.CreatedBy;
}

using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Runtime;

public interface IWorkflowSensitivePayloadStore
{
    Task SaveNodeOutputsAsync(Guid recordId, Guid runId, Guid teamId, IReadOnlyDictionary<string, JsonElement> outputs, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, JsonElement>?> ReadNodeOutputsAsync(Guid recordId, Guid runId, Guid teamId, CancellationToken cancellationToken);
}

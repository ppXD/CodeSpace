using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Defaults;

public sealed partial class StorageDefaultMaterializer
{
    /// <summary>Everything one materialization accumulates, in the order the pipeline fills it.</summary>
    private sealed class MaterializationContext
    {
        public required Guid TeamId { get; init; }
        public required string DataClassTypeKey { get; init; }
        public required Guid ActorId { get; init; }
        public required bool Automatic { get; init; }

        public StorageDefault Template { get; set; } = default!;
        public IStorageProviderModule Module { get; set; } = default!;
        public IStorageProviderTeamNamespace Subdivision { get; set; } = default!;

        public string? CredentialRef { get; set; }
        public JsonElement AssembledConfig { get; set; }
        public Guid ProfileId { get; set; }
        public int ProfileRevision { get; set; }
        public Guid RouteId { get; set; }

        /// <summary>
        /// The team's own segment of the operator's namespace root. The TEAM ID, never a slug or a name: those are
        /// editable, and a namespace that changed when a team was renamed would strand every byte already written
        /// under the old one while the profile revision that recorded it stays immutable.
        /// </summary>
        public string TeamSegment => $"team-{TeamId:N}";
    }
}

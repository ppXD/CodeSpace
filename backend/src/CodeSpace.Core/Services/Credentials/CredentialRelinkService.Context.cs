using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Credentials;

public sealed partial class CredentialRelinkService
{
    /// <summary>What one relink-all has established so far; each pipeline step reads and fills it.</summary>
    private sealed class RelinkContext
    {
        public Credential From { get; set; } = default!;
        public Credential To { get; set; } = default!;
        public List<BoundRepository> Repositories { get; set; } = new();
    }

    private sealed record BoundRepository(string ExternalId, string FullPath);
}

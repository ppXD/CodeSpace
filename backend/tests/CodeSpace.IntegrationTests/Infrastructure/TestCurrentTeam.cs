using CodeSpace.Core.Services.Identity;

namespace CodeSpace.IntegrationTests.Infrastructure;

/// <summary>Test double for <see cref="ICurrentTeam"/> — injected via PostgresFixture.BeginScope(configure).</summary>
public sealed class TestCurrentTeam : ICurrentTeam
{
    public TestCurrentTeam(Guid? id) { Id = id; }

    public Guid? Id { get; }
    public bool IsSet => Id.HasValue;
}

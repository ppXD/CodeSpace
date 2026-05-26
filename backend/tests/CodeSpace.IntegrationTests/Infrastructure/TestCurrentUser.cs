using CodeSpace.Core.Services.Identity;

namespace CodeSpace.IntegrationTests.Infrastructure;

/// <summary>
/// Test double for <see cref="ICurrentUser"/>. Tenancy tests construct one with a specific
/// Id + Roles combo to simulate any caller identity. Passed to
/// <see cref="PostgresFixture.BeginScope(Action{Autofac.ContainerBuilder})"/> so the
/// MediatR authorization behaviors see the controlled identity instead of the seeder.
/// </summary>
public sealed class TestCurrentUser : ICurrentUser
{
    public TestCurrentUser(Guid? id, string name = "test-user", params string[] roles)
    {
        Id = id;
        Name = name;
        Roles = roles;
    }

    public Guid? Id { get; }
    public string Name { get; }
    public IReadOnlyList<string> Roles { get; }

    /// <summary>Tests rarely need direct permissions; set via object initializer if needed.</summary>
    public IReadOnlyList<string> Permissions { get; init; } = Array.Empty<string>();

    /// <summary>Default false — tests opt in by setting it via initializer for rotation-gate tests.</summary>
    public bool PasswordMustChange { get; init; }

    public bool HasRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);

    public bool HasPermission(string permission) => Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
}

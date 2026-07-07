using CodeSpace.IntegrationTests.Infrastructure;

namespace CodeSpace.E2ETests.Infrastructure;

/// <summary>
/// Binds the engine-tier E2E tests (moved here from IntegrationTests) to the SHARED <see cref="PostgresFixture"/>
/// type. xUnit discovers <c>[CollectionDefinition]</c> per TEST ASSEMBLY, so the E2E assembly needs its own
/// definition over the same fixture type + the same collection NAME (<see cref="PostgresCollection.Name"/>) the
/// moved tests already carry on their <c>[Collection(...)]</c> attribute — no test-body change. A separate
/// <see cref="PostgresFixture"/> instance is created for this assembly's run (each <c>dotnet test</c> is isolated).
/// </summary>
[CollectionDefinition(PostgresCollection.Name)]
public sealed class PostgresCollectionE2E : ICollectionFixture<PostgresFixture> { }

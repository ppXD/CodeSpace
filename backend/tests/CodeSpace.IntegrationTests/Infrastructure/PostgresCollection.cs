namespace CodeSpace.IntegrationTests.Infrastructure;

[CollectionDefinition(Name)]
public class PostgresCollection : ICollectionFixture<PostgresFixture>, IClassFixture<Jobs.PerClassJobClientReset>
{
    public const string Name = "Postgres";
}

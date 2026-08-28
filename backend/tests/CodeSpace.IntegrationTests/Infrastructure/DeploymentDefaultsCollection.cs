namespace CodeSpace.IntegrationTests.Infrastructure;

/// <summary>
/// A SECOND Postgres database, for suites that own a DEPLOYMENT-scope row.
///
/// <para>A storage default is one row per data class for the whole instance, and it can never be deleted. Two test
/// classes in one collection therefore cannot both author one: whichever runs first wins, and the other sees either a
/// conflict or a revision it did not expect. That is not a flake to be ordered around — it is two suites claiming the
/// same singleton. Giving them separate databases is what makes each one's claim true.</para>
///
/// <para>The cost is one more schema build per run. Anything that does NOT own a deployment-scope row belongs in
/// <see cref="PostgresCollection"/> instead, so this stays a second database rather than the beginning of one per
/// suite.</para>
/// </summary>
[CollectionDefinition(Name)]
public class DeploymentDefaultsCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "DeploymentDefaults";
}

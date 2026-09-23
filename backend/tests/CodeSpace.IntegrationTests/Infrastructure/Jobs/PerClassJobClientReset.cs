using Autofac;

namespace CodeSpace.IntegrationTests.Infrastructure.Jobs;

/// <summary>
/// Hands every test class in a <see cref="PostgresFixture"/> collection the shared <see cref="InMemoryBackgroundJobClient"/>
/// in its constructed state. Declared as an <c>IClassFixture</c> on the collection DEFINITIONS, so xUnit builds one per
/// test class — with the collection's own fixture, before that class's first test — and a collection runs its classes
/// one at a time, so nothing touches the client between this reset and the class that owns it next.
///
/// <para>The client is one instance per fixture (SingleInstance). A class that switched it to record-only
/// (<c>AutoExecute = false</c>) and never switched it back left every later class that drains its own jobs with nothing
/// queued: <c>SupervisorTurnFlowTests</c> stayed Suspended after <c>SupervisorProjectionFlowTests</c>, and the binding
/// registrar never ran after <c>TaskRoutePreviewFlowTests</c>. Whether it bit depended only on which class happened to run
/// next, so it passed alone and failed in company. Restoring per class makes that impossible whatever a class does
/// inside itself; <see cref="InMemoryBackgroundJobClient.ManualExecution"/> keeps a test's own switch scoped to it.</para>
///
/// <para>Pinned by <c>PostgresCollectionJobClientResetConventionTests</c>: a collection over the fixture that does not
/// declare this reset reds there rather than quietly sharing one client's leftovers between its classes.</para>
/// </summary>
public sealed class PerClassJobClientReset
{
    public PerClassJobClientReset(PostgresFixture fixture)
    {
        using var scope = fixture.BeginScope();
        scope.Resolve<InMemoryBackgroundJobClient>().Reset();
    }
}

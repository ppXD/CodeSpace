using CodeSpace.IntegrationTests.Infrastructure;
using Xunit;

namespace CodeSpace.E2ETests.Infrastructure;

/// <summary>
/// Binds the engine-tier E2E tests (moved here from IntegrationTests) to the SHARED <see cref="PostgresFixture"/>
/// type. xUnit discovers <c>[CollectionDefinition]</c> per TEST ASSEMBLY, so the E2E assembly needs its own
/// definition over the same fixture type + the same collection NAME (<see cref="PostgresCollection.Name"/>) the
/// moved tests already carry on their <c>[Collection(...)]</c> attribute — no test-body change. A separate
/// <see cref="PostgresFixture"/> instance is created for this assembly's run (each <c>dotnet test</c> is isolated).
///
/// <para><b>It is also this assembly's fake-CLI serialization boundary — the load-bearing reason it must stay the
/// ONLY collection any fake-arming class carries.</b> Every fake CLI arms the PROCESS-WIDE
/// <c>CodexHarness.CommandEnvVar</c> / <c>ClaudeCodeHarness.CommandEnvVar</c> in its constructor and clears it on
/// dispose, and xUnit runs different COLLECTIONS in parallel. A fake-arming class in a second collection therefore
/// re-points the var mid-flight of this one's agents: they spawn the OTHER class's script, and the run keeps going
/// with the wrong CLI — agents that write no file (<c>result.Patch</c> null), two agents that no longer collide on
/// one file (a merge that should conflict reports Clean), or a cleared var that resolves the absent real binary
/// (Failed). Nothing throws; a DIFFERENT whole-loop assertion reds each run. That is exactly what happened while
/// the Http-surface launch tests sat in a collection of their own: they arm the same var, so they raced the
/// supervisor whole-loop arc and it flaked ~2-3 tests per full-suite run while passing 26/26 on its own.</para>
///
/// <para>The invariant is pinned from source by <c>FakeAgentCliCollectionConventionTests</c> — a new fake-arming
/// class parked in a fresh collection reds there instead of quietly corrupting a sibling's agents. Classes that
/// only READ the var (the real-CLI resume gates) need no collection: they check WHAT they resolved via
/// <c>FakeAgentCliMarker</c>, which holds regardless of who runs when.</para>
/// </summary>
[CollectionDefinition(PostgresCollection.Name)]
public sealed class PostgresCollectionE2E : ICollectionFixture<PostgresFixture> { }

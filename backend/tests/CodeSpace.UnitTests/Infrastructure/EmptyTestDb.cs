using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.UnitTests.Infrastructure;

/// <summary>
/// An EMPTY in-memory <see cref="CodeSpaceDbContext"/> for unit tests that drive a service whose DB reads are all
/// expected to return nothing.
///
/// <para>It replaces the <c>db: null!</c> these tests used to pass. That null was only ever safe because every DB
/// read in the supervisor rehydrate happened to be gated behind a predicate the tests didn't trip — so a
/// legitimate new read (D1: the run's spend accounting now folds brain-plane spend for EVERY run, not only a
/// capped one) turned 37 green tests into NullReferenceExceptions that said nothing about the change. An empty
/// context answers "no rows" honestly instead, which is what these tests actually mean.</para>
///
/// <para>Each call gets its OWN uniquely-named store, so two tests can never see each other's writes.</para>
/// </summary>
public static class EmptyTestDb
{
    public static CodeSpaceDbContext New() =>
        new(new DbContextOptionsBuilder<CodeSpaceDbContext>()
            .UseInMemoryDatabase($"unit-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);
}

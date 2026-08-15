using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Routing;

public sealed class StorageRouteConflictClassificationTests
{
    [Theory]
    [InlineData("P7501", true)]
    [InlineData(PostgresErrorCodes.UniqueViolation, true)]
    [InlineData("P0001", false)]
    public void Only_route_concurrency_and_unique_sqlstates_are_conflicts(string sqlState, bool expected)
    {
        var postgres = new PostgresException("database detail", "ERROR", "ERROR", sqlState);

        StorageRouteService.IsWriteConflict(new DbUpdateException("wrapper", postgres)).ShouldBe(expected);
        StorageRouteService.IsWriteConflict(postgres).ShouldBe(expected);
    }
}

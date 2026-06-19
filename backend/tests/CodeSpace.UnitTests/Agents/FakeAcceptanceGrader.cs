using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Test double for <see cref="ISupervisorAcceptanceGrader"/> (A3): records each call (count + last args) and returns
/// a canned grade. Defaults to a passing grade so existing turn-loop tests that never reach a resolve-with-command
/// fold are unaffected; A3's own tests configure the grade + assert the call count (proving the grade runs once).
/// </summary>
internal sealed class FakeAcceptanceGrader : ISupervisorAcceptanceGrader
{
    private readonly BenchmarkGrade _grade;
    private readonly Exception? _throw;

    public FakeAcceptanceGrader(BenchmarkGrade? grade = null) => _grade = grade ?? new BenchmarkGrade { Passed = true, Detail = "tests-passed" };
    public FakeAcceptanceGrader(Exception toThrow) { _throw = toThrow; _grade = new BenchmarkGrade { Passed = false, Detail = "unused" }; }

    public int CallCount { get; private set; }
    public (Guid RepositoryId, Guid TeamId, string Branch, IReadOnlyList<string> Command, int TimeoutSeconds)? LastCall { get; private set; }

    public Task<BenchmarkGrade> GradeAsync(Guid repositoryId, Guid teamId, string branch, IReadOnlyList<string> command, int timeoutSeconds, CancellationToken cancellationToken)
    {
        CallCount++;
        LastCall = (repositoryId, teamId, branch, command, timeoutSeconds);
        if (_throw != null) throw _throw;
        return Task.FromResult(_grade);
    }
}

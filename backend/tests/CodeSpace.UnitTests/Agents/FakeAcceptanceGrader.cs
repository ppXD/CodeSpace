using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
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
    public (Guid RepositoryId, Guid TeamId, string Branch, IReadOnlyList<string> Command, int TimeoutSeconds, BenchmarkGradingKind Kind)? LastCall { get; private set; }

    public int PatchCallCount { get; private set; }
    public (Guid RepositoryId, Guid TeamId, string BaseSha, Guid? PatchArtifactId)? LastPatchCall { get; private set; }

    public Task<BenchmarkGrade> GradeAsync(Guid repositoryId, Guid teamId, string branch, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken)
    {
        CallCount++;
        LastCall = (repositoryId, teamId, branch, spec.Command, timeoutSeconds, spec.Kind ?? BenchmarkGradingKind.TestsPass);
        if (_throw != null) throw _throw;
        return Task.FromResult(_grade);
    }

    public Task<BenchmarkGrade> GradePatchAsync(Guid repositoryId, Guid teamId, string baseSha, string inlinePatch, Guid? patchArtifactId, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken)
    {
        PatchCallCount++;
        LastPatchCall = (repositoryId, teamId, baseSha, patchArtifactId);
        if (_throw != null) throw _throw;
        return Task.FromResult(_grade);
    }

    /// <summary>S3: the baseline grade — configurable independently of the candidate grade (a differential needs the two to disagree); defaults to the candidate grade.</summary>
    public BenchmarkGrade? BaseGrade { get; set; }

    public int BaseCallCount { get; private set; }
    public (Guid RepositoryId, Guid TeamId, string BaseSha)? LastBaseCall { get; private set; }

    public Task<BenchmarkGrade> GradeBaseAsync(Guid repositoryId, Guid teamId, string baseSha, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken)
    {
        BaseCallCount++;
        LastBaseCall = (repositoryId, teamId, baseSha);
        if (_throw != null) throw _throw;
        return Task.FromResult(BaseGrade ?? _grade);
    }
}

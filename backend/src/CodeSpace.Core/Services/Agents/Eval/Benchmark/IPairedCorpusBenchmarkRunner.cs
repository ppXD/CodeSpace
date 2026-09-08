using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

public interface IPairedCorpusBenchmarkRunner
{
    Task<PairedCorpusBenchmarkRun> RunPairedAsync(PairedCorpusBenchmarkRequest request, CancellationToken cancellationToken);
}

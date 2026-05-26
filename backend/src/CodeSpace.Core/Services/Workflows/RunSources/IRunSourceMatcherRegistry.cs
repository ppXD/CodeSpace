namespace CodeSpace.Core.Services.Workflows.RunSources;

public interface IRunSourceMatcherRegistry
{
    IRunSourceMatcher? Get(string typeKey);
    IReadOnlyList<IRunSourceMatcher> All { get; }
}

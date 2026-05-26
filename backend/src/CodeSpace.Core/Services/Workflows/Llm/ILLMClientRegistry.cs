namespace CodeSpace.Core.Services.Workflows.Llm;

public interface ILLMClientRegistry
{
    ILLMClient Resolve(string provider);
    IReadOnlyList<ILLMClient> All { get; }
}

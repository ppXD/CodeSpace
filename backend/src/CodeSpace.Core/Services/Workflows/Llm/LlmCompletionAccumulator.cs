using System.Text;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// Mutable fold state for a text stream — the ONE place a <see cref="LlmStreamEvent"/> sequence collapses into an
/// <see cref="LLMCompletion"/>. Shared by <see cref="LlmTextStreamFold"/> (which drains a whole enumerable) and the
/// recording decorator (which tees each event live while building the completed-row payload), so the accumulation can
/// never drift between "what the caller receives" and "what the ledger records". TextDelta fragments concat in arrival
/// order; each Meta field is last-write-wins (a null field is a no-op).
/// </summary>
internal sealed class LlmCompletionAccumulator
{
    private readonly StringBuilder _text = new();
    private string? _model, _finishReason;
    private int? _inputTokens, _outputTokens;

    public void Add(LlmStreamEvent evt)
    {
        switch (evt)
        {
            case LlmStreamEvent.TextDelta d:
                _text.Append(d.Text);
                break;

            case LlmStreamEvent.Meta m:
                if (m.Model is not null) _model = m.Model;
                if (m.InputTokens is not null) _inputTokens = m.InputTokens;
                if (m.OutputTokens is not null) _outputTokens = m.OutputTokens;
                if (m.FinishReason is not null) _finishReason = m.FinishReason;
                break;
        }
    }

    public string Text => _text.ToString();

    public string ResolveModel(string fallbackModel) => _model ?? fallbackModel;

    public LlmUsage Usage => new() { InputTokens = _inputTokens, OutputTokens = _outputTokens, FinishReason = _finishReason };

    public LLMCompletion Build(string fallbackModel) => new() { Text = Text, Model = ResolveModel(fallbackModel), Usage = Usage };
}

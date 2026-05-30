using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

public sealed class ResumeWorkflowCallbackCommandHandler : IRequestHandler<ResumeWorkflowCallbackCommand, bool>
{
    private readonly IWorkflowResumeService _resume;

    public ResumeWorkflowCallbackCommandHandler(IWorkflowResumeService resume) { _resume = resume; }

    public async Task<bool> Handle(ResumeWorkflowCallbackCommand request, CancellationToken cancellationToken)
    {
        // Normalise the posted body to valid JSON so it round-trips through the jsonb payload
        // column and the node's `body` output is well-formed. Non-JSON bodies are wrapped.
        var payloadJson = NormalizeBody(request.Body);

        return await _resume.ResumeByCallbackTokenAsync(request.Token, payloadJson, cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "{}";

        try
        {
            using var _ = JsonDocument.Parse(body);
            return body;
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { raw = body });
        }
    }
}

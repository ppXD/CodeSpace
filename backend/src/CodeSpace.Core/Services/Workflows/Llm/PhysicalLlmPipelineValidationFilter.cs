using CodeSpace.Core.Services.Workflows.Llm.Exceptions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>Validate the final named-client pipeline, including later registration overrides, before a capped Send.</summary>
internal sealed class PhysicalLlmPipelineValidationFilter : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        if (!LlmHttpClientRegistration.ClientNames.Contains(builder.Name, StringComparer.Ordinal)) return;
        var indexed = builder.AdditionalHandlers.Select((handler, index) => (handler, index)).ToArray();
        var physical = indexed.Where(p => p.handler is PhysicalLlmAccountingHandler).ToArray();
        var retry = indexed.Where(p => p.handler is ResilienceHandler).Select(p => p.index).ToArray();
        var valid = physical.Length == 1 && retry.Length > 0 && retry.All(index => index < physical[0].index);
        builder.AdditionalHandlers.Insert(0, new PipelineGuard(valid));
    };

    private sealed class PipelineGuard(bool valid) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!valid && request.Options.TryGetValue(PhysicalLlmCallContext.DispatchKey, out _))
                throw new PhysicalLlmAccountingException("The capped provider's final HTTP pipeline is missing its per-POST handler inside retry.");
            return base.SendAsync(request, cancellationToken);
        }
    }
}

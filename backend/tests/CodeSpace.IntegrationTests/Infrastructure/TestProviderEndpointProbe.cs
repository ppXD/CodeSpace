using System.Collections.Concurrent;
using CodeSpace.Core.Services.ProviderInstances;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;

namespace CodeSpace.IntegrationTests.Infrastructure;

/// <summary>
/// The suite's <see cref="IProviderEndpointProbe"/>: it contacts nothing, so adding or editing a provider at an address
/// that only exists in a test (<c>https://gitlab.acme.test</c>) never waits on DNS or a socket. It records every address it
/// was asked about, and answers a redirect for the ones a test registered with <see cref="RedirectsTo"/> — the same typed
/// refusal the real probe throws, and for the ones registered with <see cref="NeverAnswers"/> the typed "does not answer"
/// refusal. One instance serves the fixture, so tests key their addresses by a name of their own.
/// </summary>
internal sealed class TestProviderEndpointProbe : IProviderEndpointProbe
{
    private readonly ConcurrentDictionary<string, Uri> _redirects = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _asked = new();
    private readonly ConcurrentQueue<string> _askedToAnswer = new();
    private readonly ConcurrentDictionary<string, byte> _silent = new(StringComparer.Ordinal);

    /// <summary>Every address a provider add or edit asked about, in the order it was asked.</summary>
    public IReadOnlyCollection<string> Asked => _asked;

    /// <summary>Every address an https upgrade asked whether it answers, in the order it was asked.</summary>
    public IReadOnlyCollection<string> AskedToAnswer => _askedToAnswer;

    /// <summary>The address <paramref name="url"/> answers a redirect to when it is asked about.</summary>
    public void RedirectsTo(string url, string target) => _redirects[url] = new Uri(target);

    /// <summary>The address <paramref name="url"/> never answers HTTP — a port that does not speak TLS, a host that is down.</summary>
    public void NeverAnswers(string url) => _silent[url] = 0;

    public Task EnsureNoRedirectToAnotherOriginAsync(ProviderKind provider, string url, CancellationToken cancellationToken)
    {
        _asked.Enqueue(url);

        if (_redirects.TryGetValue(url, out var target)) throw new ProviderBaseUrlRedirectsException(provider, new Uri(url.TrimEnd('/') + "/api/v4/version"), target);

        return Task.CompletedTask;
    }

    public Task EnsureAnswersAsync(ProviderKind provider, string url, CancellationToken cancellationToken)
    {
        _askedToAnswer.Enqueue(url);

        if (_silent.ContainsKey(url)) throw new ProviderEndpointUnreachableException(provider, new Uri(url), "HttpRequestException");

        return Task.CompletedTask;
    }
}

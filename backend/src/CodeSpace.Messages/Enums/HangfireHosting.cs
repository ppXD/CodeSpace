namespace CodeSpace.Messages.Enums;

/// <summary>
/// Which ROLE this process plays in the Hangfire topology — the one knob that decides whether a pod
/// PROCESSES the queue or only ENQUEUES onto it. Both roles run the same <c>CodeSpace.Api.dll</c>; the
/// deployment picks the role through the <c>HangfireHosting</c> configuration key, so the api and worker
/// Deployments scale independently off one image pair.
/// </summary>
public enum HangfireHosting
{
    /// <summary>Public HTTP surface. Storage + the job client are registered so the pod can ENQUEUE, but no Hangfire server runs, so it processes nothing — it executes no agent run and therefore opens no per-run MCP endpoint or sandbox.</summary>
    Api,

    /// <summary>The processing pod: runs the Hangfire servers, owns recurring-job scheduling, and executes agent runs (and with them the per-run MCP endpoint). The DEFAULT, so a single all-in-one process — local <c>dotnet run</c>, or a one-pod deployment — behaves exactly as before this key existed.</summary>
    Worker,
}

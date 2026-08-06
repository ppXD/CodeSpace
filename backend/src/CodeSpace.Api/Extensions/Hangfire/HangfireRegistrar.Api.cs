using CodeSpace.Messages.Enums;
using Hangfire;
using Serilog;

namespace CodeSpace.Api.Extensions.Hangfire;

/// <summary>
/// The PUBLIC role: storage + serializer + the job client only, so this pod ENQUEUES onto the shared queue and
/// processes nothing. Selected by <c>HangfireHosting=Api</c>.
///
/// <para>No <c>AddHangfireServer</c> is the whole mechanism — without a server there are no workers, so no job of
/// any queue is fetched here, and no recurring schedule is owned here either. Agent runs are Hangfire jobs, so this
/// pod never reaches <c>AgentRunExecutor.ExecuteAsync</c> and therefore never opens a per-run MCP endpoint, sandbox
/// or UDS socket. That is the security property the api/worker image split exists for: the internet-facing pod
/// carries none of the agent-execution surface.</para>
///
/// <para>The dashboard IS mounted — an admin looking at the public pod should still be able to SEE the queue. It is
/// a read/control view over shared storage, not a processing capability.</para>
/// </summary>
public class ApiHangfireRegistrar : HangfireRegistrarBase
{
    public override void ApplyHangfire(IApplicationBuilder app, IConfiguration configuration)
    {
        base.ApplyHangfire(app, configuration);

        app.UseHangfireDashboard("/hangfire", new DashboardOptions
        {
            Authorization = new[] { new HangfireDashboardAuthFilter() },
            DashboardTitle = "CodeSpace jobs",
        });

        Log.Information("Hangfire role {Role}: this pod enqueues and serves the dashboard, but processes no jobs", HangfireHosting.Api);
    }
}

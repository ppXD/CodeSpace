using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// "Run on a schedule" trigger. Unlike the PR / push triggers, there's no inbound webhook — a
/// recurring producer (<c>ScheduleTriggerRecurringJob</c> → <c>IScheduleTriggerService</c>) ticks
/// every minute, finds <c>trigger.schedule</c> activations whose cron is due, and fires a run. This
/// node only exposes the trigger payload (the scheduled instant + the cron) as outputs.
///
/// Lives in Core Flow (not a git/event plugin) because scheduling is domain-agnostic — every
/// deployment can run a workflow on a timer regardless of which providers are connected.
/// </summary>
public sealed class TriggerScheduleNode : INodeRuntime
{
    public string TypeKey => "trigger.schedule";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "On a schedule",
        Category = "Triggers",
        Kind = NodeKind.Trigger,
        IconKey = "clock",
        Description = "Starts the workflow on a recurring schedule (cron).",
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "cron": {
                  "type": "string",
                  "description": "Standard 5-field cron in UTC (minute hour day-of-month month day-of-week). Examples: '*/15 * * * *' every 15 min; '0 9 * * 1-5' weekdays at 09:00.",
                  "x-spotlight": 1
                }
              },
              "required": ["cron"],
              "additionalProperties": false
            }
            """),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "scheduledFor": { "type": "string", "description": "ISO-8601 UTC instant this run was scheduled for." },
                "cron": { "type": "string" }
              }
            }
            """)
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        var outputs = context.Scope.Trigger.ToDictionary(kv => kv.Key, kv => kv.Value);
        return Task.FromResult(NodeResult.Ok(outputs));
    }
}

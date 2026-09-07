using System.Globalization;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace CodeSpace.E2ETests.Workflows;

internal sealed record BenchmarkRunEvidenceQuery(Guid TeamId, Guid? FinalRunId, string WorkspaceDirectory, int Limit);

/// <summary>PostgreSQL still detoasts JSON roots, but only bounded scalar metadata crosses into the exporter.</summary>
internal static class BenchmarkRunEvidenceReader
{
    internal const int ExitReasonBytes = 2_048;
    private const string ProjectionSql = """
        SELECT r.id, r.status, r.reattach_attempts,
            COALESCE(jsonb_typeof(r.result_jsonb), 'missing') AS result_kind,
            COALESCE(jsonb_typeof(r.result_jsonb -> 'model'), 'missing') AS model_kind,
            CASE WHEN jsonb_typeof(r.result_jsonb -> 'model') = 'string' THEN octet_length(r.result_jsonb ->> 'model') END AS model_bytes,
            CASE WHEN jsonb_typeof(r.result_jsonb -> 'model') = 'string' AND octet_length(r.result_jsonb ->> 'model') <= @model_bytes THEN r.result_jsonb ->> 'model' END AS model,
            COALESCE(jsonb_typeof(r.result_jsonb -> 'exitReason'), 'missing') AS exit_kind,
            CASE WHEN jsonb_typeof(r.result_jsonb -> 'exitReason') = 'string' THEN octet_length(r.result_jsonb ->> 'exitReason') END AS exit_bytes,
            CASE WHEN jsonb_typeof(r.result_jsonb -> 'exitReason') = 'string' AND octet_length(r.result_jsonb ->> 'exitReason') <= @exit_bytes THEN r.result_jsonb ->> 'exitReason' END AS exit_reason,
            COALESCE(jsonb_typeof(r.result_jsonb -> 'tokenUsage'), 'missing') AS usage_kind,
            COALESCE(jsonb_typeof(r.result_jsonb -> 'tokenUsage' -> 'inputTokens'), 'missing') AS input_kind,
            CASE WHEN jsonb_typeof(r.result_jsonb -> 'tokenUsage' -> 'inputTokens') = 'number' AND octet_length(r.result_jsonb -> 'tokenUsage' ->> 'inputTokens') <= 11 THEN r.result_jsonb -> 'tokenUsage' ->> 'inputTokens' END AS input_text,
            COALESCE(jsonb_typeof(r.result_jsonb -> 'tokenUsage' -> 'outputTokens'), 'missing') AS output_kind,
            CASE WHEN jsonb_typeof(r.result_jsonb -> 'tokenUsage' -> 'outputTokens') = 'number' AND octet_length(r.result_jsonb -> 'tokenUsage' ->> 'outputTokens') <= 11 THEN r.result_jsonb -> 'tokenUsage' ->> 'outputTokens' END AS output_text,
            CASE WHEN jsonb_typeof(r.result_jsonb -> 'reviseRounds') = 'number' AND octet_length(r.result_jsonb ->> 'reviseRounds') <= 11 THEN r.result_jsonb ->> 'reviseRounds' END AS revise_text
        FROM agent_run AS r
        WHERE r.team_id = @team_id
          AND ((@final_id IS NOT NULL AND r.id = @final_id) OR (@final_id IS NULL AND r.task_jsonb @> @hint::jsonb))
        ORDER BY r.created_date, r.id
        LIMIT @take
        """;

    internal static async Task<BenchmarkRunEvidence[]> ReadAsync(CodeSpaceDbContext db, BenchmarkRunEvidenceQuery query, CancellationToken cancellationToken)
    {
        var hint = JsonSerializer.Serialize(new { workspaceDirectory = query.WorkspaceDirectory }, AgentJson.Options);
        var rows = await db.Database.SqlQueryRaw<DbRow>(ProjectionSql,
        [
            new NpgsqlParameter<Guid>("team_id", query.TeamId), new NpgsqlParameter("final_id", NpgsqlDbType.Uuid) { Value = (object?)query.FinalRunId ?? DBNull.Value },
            new NpgsqlParameter<string>("hint", hint), new NpgsqlParameter<int>("take", Math.Clamp(query.Limit, 1, BenchmarkEvidenceOptions.CandidateLimit)),
            new NpgsqlParameter<int>("model_bytes", BenchmarkEvidenceOptions.MaximumSecretBytes), new NpgsqlParameter<int>("exit_bytes", ExitReasonBytes),
        ]).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(Project).ToArray();
    }

    private static BenchmarkRunEvidence Project(DbRow row)
    {
        var input = ReadCount(row.InputKind, row.InputText);
        var output = ReadCount(row.OutputKind, row.OutputText);
        var validUsage = row.ResultKind == "object" && row.UsageKind == "object" && input is not null && output is not null;
        var resultAvailability = row.ResultKind == "object" ? "available" : row.ResultKind is "missing" or "null" ? "unknown" : "unknown-invalid-shape";
        var usageAvailability = validUsage ? "reported" : resultAvailability == "unknown" || (row.ResultKind == "object" && row.UsageKind is ("missing" or "null")) ? "unknown" : "unknown-invalid-shape";
        return new BenchmarkRunEvidence(row.Id, row.Status, row.ReattachAttempts)
        {
            ResultAvailability = resultAvailability, Model = row.Model, ModelAvailability = TextAvailability(row.ModelKind, row.ModelBytes, BenchmarkEvidenceOptions.MaximumSecretBytes),
            ModelExceededBound = row.ModelBytes > BenchmarkEvidenceOptions.MaximumSecretBytes, ExitReason = row.ExitReason, ExitReasonAvailability = TextAvailability(row.ExitKind, row.ExitBytes, ExitReasonBytes),
            Usage = validUsage ? new AgentTokenUsage { InputTokens = input!.Value, OutputTokens = output!.Value } : null, UsageAvailability = usageAvailability, ReviseRounds = ReadCount("number", row.ReviseText),
        };
    }

    private static int? ReadCount(string kind, string? value) => kind == "number" && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    private static string TextAvailability(string kind, int? bytes, int limit) => kind == "string" ? bytes > limit ? "unknown-field-exceeds-bound" : "available" : kind is "missing" or "null" ? "unknown" : "unknown-invalid-shape";

    private sealed class DbRow
    {
        public Guid Id { get; set; }
        public string Status { get; set; } = "";
        public int ReattachAttempts { get; set; }
        public string ResultKind { get; set; } = "";
        public string ModelKind { get; set; } = "";
        public string? Model { get; set; }
        public int? ModelBytes { get; set; }
        public string ExitKind { get; set; } = "";
        public string? ExitReason { get; set; }
        public int? ExitBytes { get; set; }
        public string UsageKind { get; set; } = "";
        public string InputKind { get; set; } = "";
        public string? InputText { get; set; }
        public string OutputKind { get; set; } = "";
        public string? OutputText { get; set; }
        public string? ReviseText { get; set; }
    }
}

internal sealed record BenchmarkRunEvidence(Guid Id, string Status, int ReattachAttempts)
{
    public required string ResultAvailability { get; init; }
    public string? Model { get; init; }
    public required string ModelAvailability { get; init; }
    public bool ModelExceededBound { get; init; }
    public string? ExitReason { get; init; }
    public required string ExitReasonAvailability { get; init; }
    public AgentTokenUsage? Usage { get; init; }
    public required string UsageAvailability { get; init; }
    public int? ReviseRounds { get; init; }
}

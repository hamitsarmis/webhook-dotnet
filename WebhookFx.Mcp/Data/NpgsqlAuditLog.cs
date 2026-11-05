using Npgsql;

namespace WebhookFx.Mcp.Data;

public sealed class NpgsqlAuditLog : IAuditLog
{
    private readonly McpSettings _settings;

    public NpgsqlAuditLog(McpSettings settings)
    {
        _settings = settings;
    }

    public async Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.ConnectionString))
            throw new InvalidOperationException("ConnectionStrings__Default is not set.");

        await using var conn = new NpgsqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "AgentActions" ("tool", "args", "dry_run", "outcome", "correlation_id")
            VALUES (@tool, @args::jsonb, @dry_run, @outcome, @correlation_id)
            """;
        cmd.Parameters.AddWithValue("tool", entry.Tool);
        cmd.Parameters.AddWithValue("args", entry.ArgumentsJson);
        cmd.Parameters.AddWithValue("dry_run", entry.DryRun);
        cmd.Parameters.AddWithValue("outcome", entry.Outcome);
        cmd.Parameters.AddWithValue("correlation_id", entry.CorrelationId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}

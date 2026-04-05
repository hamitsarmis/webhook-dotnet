using System.Text;
using Npgsql;
using RabbitMQ.Client;

namespace OutboxProcessor;

public class OutboxPollingService : BackgroundService
{
    private readonly string _connectionString;
    private readonly string _rabbitHost;
    private readonly int _pollingIntervalMs;
    private readonly int _batchSize;
    private readonly ILogger<OutboxPollingService> _logger;

    public OutboxPollingService(IConfiguration configuration, ILogger<OutboxPollingService> logger)
    {
        _connectionString = configuration.GetConnectionString("Default")!;
        _rabbitHost = configuration["RabbitMQ:Host"] ?? "localhost";
        _pollingIntervalMs = int.Parse(configuration["Polling:IntervalMs"] ?? "5000");
        _batchSize = int.Parse(configuration["Polling:BatchSize"] ?? "50");
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OutboxPollingService started. Polling every {Interval}ms", _pollingIntervalMs);

        var factory = new ConnectionFactory { HostName = _rabbitHost };
        await using var rabbitConnection = await factory.CreateConnectionAsync(stoppingToken);
        await using var channel = await rabbitConnection.CreateChannelAsync(cancellationToken: stoppingToken);

        await channel.ExchangeDeclareAsync(
            exchange: "signals",
            type: ExchangeType.Topic,
            durable: true,
            cancellationToken: stoppingToken);

        await channel.QueueDeclareAsync(
            queue: "signals.process",
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: stoppingToken);

        await channel.QueueBindAsync(
            queue: "signals.process",
            exchange: "signals",
            routingKey: "signal.#",
            cancellationToken: stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAndPublishAsync(channel, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during outbox polling");
            }

            await Task.Delay(_pollingIntervalMs, stoppingToken);
        }
    }

    private async Task PollAndPublishAsync(IChannel channel, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var selectCmd = conn.CreateCommand();
        selectCmd.CommandText = """
            SELECT "Id", "SignalId", "EventType", "Payload", "IdempotencyKey"
            FROM "OutboxEvents"
            WHERE "ProcessedAt" IS NULL AND "FailedAt" IS NULL
            ORDER BY "CreatedAt" ASC
            LIMIT @batch_size
            """;
        selectCmd.Parameters.AddWithValue("batch_size", _batchSize);

        var events = new List<OutboxEvent>();
        await using var reader = await selectCmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            events.Add(new OutboxEvent
            {
                Id = reader.GetInt32(0),
                SignalId = reader.GetInt32(1),
                EventType = reader.GetString(2),
                Payload = reader.GetString(3),
                IdempotencyKey = reader.IsDBNull(4) ? null : reader.GetString(4)
            });
        }
        await reader.CloseAsync();

        if (events.Count == 0)
            return;

        _logger.LogInformation("Processing {Count} outbox events", events.Count);

        foreach (var evt in events)
        {
            try
            {
                var props = new BasicProperties
                {
                    Persistent = true,
                    MessageId = evt.IdempotencyKey ?? evt.Id.ToString(),
                    ContentType = "application/json"
                };

                await channel.BasicPublishAsync(
                    exchange: "signals",
                    routingKey: evt.EventType,
                    mandatory: false,
                    basicProperties: props,
                    body: Encoding.UTF8.GetBytes(evt.Payload),
                    cancellationToken: ct);

                await MarkProcessedAsync(conn, evt.Id, ct);
                _logger.LogInformation("Published event {Id} ({EventType}) for signal {SignalId}", evt.Id, evt.EventType, evt.SignalId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish event {Id}", evt.Id);
                await MarkFailedAsync(conn, evt.Id, ex.Message, ct);
            }
        }
    }

    private static async Task MarkProcessedAsync(NpgsqlConnection conn, int eventId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE "OutboxEvents"
            SET "ProcessedAt" = NOW()
            WHERE "Id" = @id
            """;
        cmd.Parameters.AddWithValue("id", eventId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task MarkFailedAsync(NpgsqlConnection conn, int eventId, string error, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE "OutboxEvents"
            SET "FailedAt" = NOW(), "RetryCount" = "RetryCount" + 1, "ErrorMessage" = @error
            WHERE "Id" = @id
            """;
        cmd.Parameters.AddWithValue("id", eventId);
        cmd.Parameters.AddWithValue("error", error);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private record OutboxEvent
    {
        public int Id { get; init; }
        public int SignalId { get; init; }
        public string EventType { get; init; } = default!;
        public string Payload { get; init; } = default!;
        public string? IdempotencyKey { get; init; }
    }
}

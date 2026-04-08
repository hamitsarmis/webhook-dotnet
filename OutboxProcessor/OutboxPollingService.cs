using System.Text;
using System.Text.Json;
using Npgsql;
using RabbitMQ.Client;

namespace OutboxProcessor;

public class OutboxPollingService : BackgroundService
{
    private readonly string _connectionString;
    private readonly string _rabbitHost;
    private readonly int _fallbackIntervalMs;
    private readonly int _batchSize;
    private readonly ILogger<OutboxPollingService> _logger;

    public OutboxPollingService(IConfiguration configuration, ILogger<OutboxPollingService> logger)
    {
        _connectionString = configuration.GetConnectionString("Default")!;
        _rabbitHost = configuration["RabbitMQ:Host"] ?? "localhost";
        _fallbackIntervalMs = int.Parse(configuration["Polling:FallbackIntervalMs"] ?? "30000");
        _batchSize = int.Parse(configuration["Polling:BatchSize"] ?? "50");
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "OutboxPollingService started. Using LISTEN/NOTIFY with {Fallback}ms fallback poll",
            _fallbackIntervalMs);

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

        // Dedicated Npgsql connection for LISTEN — must stay open
        await using var listenConn = new NpgsqlConnection(_connectionString);
        await listenConn.OpenAsync(stoppingToken);

        await using (var listenCmd = listenConn.CreateCommand())
        {
            listenCmd.CommandText = "LISTEN outbox_new;";
            await listenCmd.ExecuteNonQueryAsync(stoppingToken);
        }

        _logger.LogInformation("Listening on PostgreSQL channel 'outbox_new'");

        var notificationReceived = new SemaphoreSlim(0);
        listenConn.Notification += (_, _) => notificationReceived.Release();

        // Process any events that were inserted before we started listening
        await ProcessPendingEventsAsync(channel, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for a notification or fallback timeout
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeoutCts.CancelAfter(_fallbackIntervalMs);

                try
                {
                    await listenConn.WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Fallback timeout — just poll
                }

                // Drain the semaphore so we don't loop unnecessarily
                while (notificationReceived.CurrentCount > 0)
                    notificationReceived.Wait(0);

                await ProcessPendingEventsAsync(channel, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during outbox processing");
                await Task.Delay(1000, stoppingToken); // back off on error
            }
        }
    }

    private async Task ProcessPendingEventsAsync(IChannel channel, CancellationToken ct)
    {
        // Process in a loop until no more pending events (handles bursts > batch size)
        while (true)
        {
            var processed = await PollAndPublishAsync(channel, ct);
            if (processed == 0)
                break;
        }
    }

    private async Task<int> PollAndPublishAsync(IChannel channel, CancellationToken ct)
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
            return 0;

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
                    body: Encoding.UTF8.GetBytes(JsonSerializer.Serialize(evt)),
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

        return events.Count;
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

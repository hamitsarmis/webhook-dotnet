using OutboxProcessor;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        services.AddHostedService<OutboxPollingService>();
    });

var host = builder.Build();
await host.RunAsync();

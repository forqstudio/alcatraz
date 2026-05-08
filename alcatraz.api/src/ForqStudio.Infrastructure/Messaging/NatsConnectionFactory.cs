using NATS.Client.Core;
using Microsoft.Extensions.Options;

namespace ForqStudio.Infrastructure.Messaging;

internal sealed class NatsConnectionFactory(IOptions<NatsOptions> natsOptions) : IAsyncDisposable
{
    private readonly NatsOptions natsOptions = natsOptions.Value;
    private readonly SemaphoreSlim gate = new(1, 1);
    private NatsConnection? connection;

    public async Task<NatsConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (connection is { ConnectionState: NatsConnectionState.Open })
        {
            return connection;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (connection is null)
            {
                connection = new NatsConnection(new NatsOpts { Url = natsOptions.Url });
            }

            if (connection.ConnectionState != NatsConnectionState.Open)
            {
                await connection.ConnectAsync();
            }

            return connection;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (connection is not null)
        {
            await connection.DisposeAsync();
            connection = null;
        }

        gate.Dispose();
    }
}

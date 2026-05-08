using ForqStudio.Application.Abstractions.Caching;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;
using System.Buffers;
using System.Text.Json;

namespace ForqStudio.Infrastructure.Caching;

internal sealed class CacheService(
    IDistributedCache cache,
    IConnectionMultiplexer connectionMultiplexer) : ICacheService
{
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        byte[]? bytes = await cache.GetAsync(key, cancellationToken);

        return bytes is null ? default : Deserialize<T>(bytes);
    }

    public Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default)
    {
        byte[] bytes = Serialize(value);

        return cache.SetAsync(key, bytes, CacheOptions.Create(expiration), cancellationToken);
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        cache.RemoveAsync(key, cancellationToken);

    public async Task RemoveManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        var redisKeys = keys.Select(k => new RedisKey(k)).ToArray();

        if (redisKeys.Length == 0)
        {
            return;
        }

        var database = connectionMultiplexer.GetDatabase();
        await database.KeyDeleteAsync(redisKeys);
    }

    private static T Deserialize<T>(byte[] bytes)
    {
        return JsonSerializer.Deserialize<T>(bytes)!;
    }

    private static byte[] Serialize<T>(T value)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using var writer = new Utf8JsonWriter(buffer);

        JsonSerializer.Serialize(writer, value);

        return buffer.WrittenSpan.ToArray();
    }
}

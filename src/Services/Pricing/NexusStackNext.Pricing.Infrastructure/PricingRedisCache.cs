using System.Text.Json;
using Microsoft.Extensions.Logging;
using NexusStackNext.Pricing.Application;
using StackExchange.Redis;

namespace NexusStackNext.Pricing.Infrastructure;

// 一个键同时保存填充令牌或已发布结果。删除/淘汰/过期都撤销填充权。
internal sealed partial class PricingRedisCache : IAsyncDisposable
{
    private const string AcquireScript = """
        local value = redis.call('HGET', KEYS[1], 'value')
        if value then return {1, value} end
        if redis.call('HEXISTS', KEYS[1], 'token') == 1 then return {2, ''} end
        redis.call('HSET', KEYS[1], 'token', ARGV[1])
        redis.call('PEXPIRE', KEYS[1], ARGV[2])
        return {0, ''}
        """;
    private const string FillScript = """
        -- nsn-pricing-fill
        if redis.call('HGET', KEYS[1], 'token') ~= ARGV[1] then return 0 end
        redis.call('HDEL', KEYS[1], 'token')
        redis.call('HSET', KEYS[1], 'value', ARGV[2])
        redis.call('PEXPIRE', KEYS[1], ARGV[3])
        return 1
        """;
    private readonly PricingCacheOptions _options;
    private readonly ILogger<PricingRedisCache> _logger;
    private readonly Lazy<Task<ConnectionMultiplexer>>? _connection;
    private long _retryAfter;

    public PricingRedisCache(PricingCacheOptions options, ILogger<PricingRedisCache> logger)
    {
        _options = options;
        _logger = logger;
        if (!options.Enabled) { return; }
        ConfigurationOptions configuration;
        try { configuration = ConfigurationOptions.Parse(options.ConnectionString); }
        catch (ArgumentException) { throw new InvalidOperationException("Pricing Redis 连接配置格式无效。"); }
        configuration.AbortOnConnectFail = false;
        configuration.BacklogPolicy = BacklogPolicy.FailFast;
        configuration.ConnectRetry = 0;
        configuration.ConnectTimeout = 1000;
        configuration.AsyncTimeout = (int)options.RedisTimeout.TotalMilliseconds;
        configuration.HeartbeatInterval = TimeSpan.FromMilliseconds(100);
        configuration.ClientName = "nsn-pricing-cache";
        _connection = new Lazy<Task<ConnectionMultiplexer>>(() => ConnectionMultiplexer.ConnectAsync(configuration));
    }

    public async Task<(PriceQuoteView? Value, string? Token)> ReadOrAcquireAsync(Guid itemId, CancellationToken cancellationToken)
    {
        if (_connection is null || BackingOff) { return default; }
        try
        {
            var database = await DatabaseAsync(cancellationToken).ConfigureAwait(false);
            var token = Guid.NewGuid().ToString("N");
            var response = (RedisResult[])(await database.ScriptEvaluateAsync(AcquireScript, [Key(itemId)],
                [token, (long)(_options.LoadTimeout + _options.RedisTimeout * 2).TotalMilliseconds])
                .WaitAsync(_options.RedisTimeout, cancellationToken).ConfigureAwait(false))!;
            if ((int)response[0] == 0) { return (null, token); }
            if ((int)response[0] != 1) { return default; }
            var value = JsonSerializer.Deserialize<PriceQuoteView>((string)response[1]!);
            return value?.ItemId == itemId ? (value, null) : default;
        }
        catch (Exception error) when (IsCacheFailure(error)) { BackOff(); return default; }
    }

    public async Task FillAsync(PriceQuoteView value, string token, CancellationToken cancellationToken)
    {
        if (BackingOff) { return; }
        try
        {
            var payload = JsonSerializer.Serialize(value);
            // 当前投影很小；禁止意外扩展成无限大缓存值。
            if (System.Text.Encoding.UTF8.GetByteCount(payload) > 16 * 1024) { return; }
            var database = await DatabaseAsync(cancellationToken).ConfigureAwait(false);
            await database.ScriptEvaluateAsync(FillScript, [Key(value.ItemId)], [token, payload, (long)_options.Ttl.TotalMilliseconds])
                .WaitAsync(_options.RedisTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (IsCacheFailure(error)) { BackOff(); }
    }

    public async Task<bool> InvalidateAsync(Guid itemId, CancellationToken cancellationToken)
    {
        if (BackingOff) { return false; }
        try
        {
            var database = await DatabaseAsync(cancellationToken).ConfigureAwait(false);
            await database.KeyDeleteAsync(Key(itemId)).WaitAsync(_options.RedisTimeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (IsCacheFailure(error)) { BackOff(); return false; }
    }

    private async Task<IDatabase> DatabaseAsync(CancellationToken token) =>
        (await _connection!.Value.WaitAsync(_options.RedisTimeout, token).ConfigureAwait(false)).GetDatabase();
    private RedisKey Key(Guid itemId) => $"{_options.Namespace}:pricing:quote:v1:{itemId:N}";
    private static bool IsCacheFailure(Exception error) => error is RedisException or TimeoutException or JsonException;
    private bool BackingOff => Environment.TickCount64 < Volatile.Read(ref _retryAfter);
    private void BackOff()
    {
        var now = Environment.TickCount64;
        if (Interlocked.Exchange(ref _retryAfter, now + 1000) <= now) { CacheUnavailable(_logger); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is { IsValueCreated: true })
        {
            try { await (await _connection.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
            catch (RedisException) { }
        }
    }

    [LoggerMessage(10, LogLevel.Warning, "Pricing Redis unavailable; using bounded database reads. Cache invalidations remain durable.")]
    private static partial void CacheUnavailable(ILogger logger);
}

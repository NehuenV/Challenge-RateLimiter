using RateLimiter.Config;
using StackExchange.Redis;

namespace RateLimiter.Storage;

public sealed class RedisBucketStore : IBucketStore
{
    private readonly IDatabase _db;

    //script central de lua, se ejecuta todo del lado de redis, aprovechando que es single thread desaparecen los 
    //race condition, el timer de redis evita la diferencia de los relojes de cada posible instancia de nustro api
    //multiplicamos por 1000 para evitar problemas de decimales 
    //mentenemos coherencia con el sistema in memory
    //PEXPIRE limpia de forma automatica las ip para no acumular basura, seria como el cache de darle x tiempo de vida
    //threshold de 1.0 - 1e-9 previene errores de redondeo de decimales en Lua, algo asi como restarle una parte de millon al 
    //1 y fijarse si es igual a nuestro numero nuevo
    //esto funciona bien con una unica instancia redis o un entorno de clusters (muchos nodos)
    private static readonly string Script = @"
        local key       = KEYS[1]
        local capacity  = tonumber(ARGV[1])
        local rate      = tonumber(ARGV[2])
        local threshold = tonumber(ARGV[3])

        local t      = redis.call('TIME')
        local now_ms = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)

        local data    = redis.call('HMGET', key, 'tokens', 'ts')
        local tokens  = tonumber(data[1]) or capacity
        local last_ts = tonumber(data[2]) or now_ms

        local elapsed  = math.max(0, (now_ms - last_ts) / 1000.0)
        local refilled = math.min(capacity, tokens + elapsed * rate)

        local allowed     = 0
        local retry_after = 0

        if refilled >= threshold then
            refilled = refilled - 1
            allowed  = 1
        else
            retry_after = (1.0 - refilled) / rate
        end

        local ttl_ms = math.ceil(capacity / rate * 1000)
        redis.call('HMSET', key, 'tokens', tostring(refilled), 'ts', tostring(now_ms))
        redis.call('PEXPIRE', key, ttl_ms)

        return { allowed, math.floor(refilled * 1000), math.floor(retry_after * 1000) }
    ";

    private const double LuaThreshold = 1.0 - 1e-9;

    public RedisBucketStore(IConnectionMultiplexer redis) =>
        _db = redis.GetDatabase();

    public async Task<BucketDecision> GetAndUpdateAsync(string key, RateLimitRule rule)
    {
        var raw = (RedisResult[]?) await _db.ScriptEvaluateAsync(
            Script,
            new RedisKey[]   { key },
            new RedisValue[] { rule.Capacity, rule.RefillRate, LuaThreshold })
            ?? throw new InvalidOperationException("El script Lua retornó null inesperadamente");

        // Los floats se dividen por 1000 para revertir la conversión hecha en el script Lua
        var allowed    = (int)  raw[0] == 1;
        var remaining  = (long) raw[1] / 1000.0;
        var retryAfter = (long) raw[2] / 1000.0;

        return new BucketDecision(allowed, remaining, retryAfter);
    }
}

using RateLimiter.Config;

namespace RateLimiter.Storage;

public interface IBucketStore
{
    // Lee el estado actual del bucket, evalúa la regla, persiste el nuevo estado
    // y devuelve la decisión — todo de forma atómica.
    // Cada implementación obtiene el tiempo internamente:
    //   InMemoryBucketStore  → TimeProvider inyectado (reemplazable en tests)
    //   RedisBucketStore     → redis.call('TIME') dentro del script Lua
    Task<BucketDecision> GetAndUpdateAsync(string key, RateLimitRule rule);
}

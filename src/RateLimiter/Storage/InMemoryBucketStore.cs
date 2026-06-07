using Microsoft.Extensions.Caching.Memory;
using RateLimiter.Algorithm;
using RateLimiter.Config;

namespace RateLimiter.Storage;

public sealed class InMemoryBucketStore : IBucketStore
{
    private readonly IMemoryCache    _cache;
    private readonly TimeProvider    _time;
    private readonly SemaphoreSlim[] _locks;
    private readonly int             _maxEntries;
    private int                      _entryCount;

    public InMemoryBucketStore(IMemoryCache cache, TimeProvider time, RateLimiterOptions options)
    {
        _cache      = cache;
        _time       = time;
        _maxEntries = options.InMemory.MaxEntries;

        //locks limitados a la configuracion. Se determina cual usar en base al hash y resto de la clave
        //si dos clientes caen en el mismo lock, afectaria un poco a la concurrencia pero no a la consistencia
        _locks = Enumerable
            .Range(0, options.InMemory.LockPoolSize)
            .Select(_ => new SemaphoreSlim(1, 1))
            .ToArray();
    }

    public async Task<BucketDecision> GetAndUpdateAsync(string key, RateLimitRule rule)
    {
        // & int.MaxValue elimina el bit de signo sin riesgo de overflow.
        // Math.Abs(int.MinValue) lanzaría OverflowException porque -(-2.147.483.648)
        // desborda el rango de int.
        var slot = (key.GetHashCode() & int.MaxValue) % _locks.Length;
        var sem  = _locks[slot];

        await sem.WaitAsync();
        try
        {

            var nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
            var isNew = !_cache.TryGetValue(key, out TokenBucket.State? current);

            //si el cache esta lleno por posibles ataques solo prohibo la entrada a los nuevos
            if (isNew && _entryCount >= _maxEntries)
                return new BucketDecision(false, 0, 1.0);
            //si no encontre al cliente le genero un bucket nuevo a maxima capacidad
            current ??= new TokenBucket.State(rule.Capacity, nowMs);

            var decision   = TokenBucket.Evaluate(current, rule, nowMs);

            var expiration = TimeSpan.FromSeconds(rule.Capacity / rule.RefillRate * 2);
            var cacheOptions = new MemoryCacheEntryOptions
            {
                SlidingExpiration = expiration,
                Priority          = CacheItemPriority.NeverRemove,
                Size              = 1
            };

            if (isNew)
            {
                Interlocked.Increment(ref _entryCount);
                //cuando el  cache borre al cliente tambien tiene la orden de restar 1 al contador global
                cacheOptions.RegisterPostEvictionCallback((_, _, _, _) =>
                    Interlocked.Decrement(ref _entryCount));
            }

            _cache.Set(key, decision.NewState, cacheOptions);
            return new BucketDecision(decision.Allowed, decision.RemainingTokens, decision.RetryAfterSeconds);
        }
        finally
        {
            sem.Release();
        }
    }
}

using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Time.Testing;
using RateLimiter.Config;
using RateLimiter.Storage;

namespace RateLimiter.Tests;

public class InMemoryBucketStoreTests
{
    private static readonly RateLimitRule Rule = new("test", Capacity: 5, RefillRate: 1.0);

    private static readonly RateLimiterOptions Opciones = new()
    {
        InMemory = new RateLimiterOptions.InMemorySettings
        {
            LockPoolSize = 16,    // suficiente para tests; 64 es para producción
            MaxEntries   = 500    // realista para tests; evita que SizeLimit y MaxEntries diverjan
        }
    };

    private static (InMemoryBucketStore Store, FakeTimeProvider Time) CreateStore(long nowMs = 0)
    {
        //carga de la misma configuracion para todos los test
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = Opciones.InMemory.MaxEntries });
        var time  = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(nowMs));
        return (new InMemoryBucketStore(cache, time, Opciones), time);
    }

    [Fact]
    public async Task PrimerRequest_ClienteNuevo_SePermite()
    {
        var (store, _) = CreateStore();

        var decision = await store.GetAndUpdateAsync("cliente1", Rule);

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task BucketAgotado_RequestRechazado()
    {
        var (store, _) = CreateStore();

        for (var i = 0; i < 5; i++)
            await store.GetAndUpdateAsync("cliente1", Rule);

        var decision = await store.GetAndUpdateAsync("cliente1", Rule);

        decision.Allowed.Should().BeFalse();
        decision.RetryAfterSeconds.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ClientesDistintos_TienenBucketsIndependientes()
    {
        var (store, _) = CreateStore();

        for (var i = 0; i < 5; i++)
            await store.GetAndUpdateAsync("cliente1", Rule);

        // cliente2 no debe verse afectado por el agotamiento de cliente1
        var decision = await store.GetAndUpdateAsync("cliente2", Rule);

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task TiempoTranscurrido_RecargaTokens()
    {
        var (store, time) = CreateStore(nowMs: 0);

        for (var i = 0; i < 5; i++)
            await store.GetAndUpdateAsync("cliente1", Rule);

        // salto en el tiempo para recargar 6 tokens (maximo en 5)
        time.Advance(TimeSpan.FromSeconds(6));

        var decision = await store.GetAndUpdateAsync("cliente1", Rule);

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Concurrencia_SinRaceCondition()
    {
        var (store, _) = CreateStore();
        var rule       = Rule with { Capacity = 10 };

        // 20 requests concurrentes — exactamente 10 deben ser permitidos
        var tasks   = Enumerable.Range(0, 20).Select(_ => store.GetAndUpdateAsync("cliente1", rule));
        var results = await Task.WhenAll(tasks);

        results.Count(r => r.Allowed).Should().Be(10);
    }

    [Fact]
    public async Task CacheLleno_ClienteNuevo_EsRechazado()
    {
        //aca creamos el store a mano porque si usamos la configuracion por defecto habria que lanzar muchas peticiones
        var opciones = new RateLimiterOptions
        {
            InMemory = new RateLimiterOptions.InMemorySettings { LockPoolSize = 4, MaxEntries = 2 }
        };
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 2 });
        var time  = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryBucketStore(cache, time, opciones);

        await store.GetAndUpdateAsync("cliente1", Rule);
        await store.GetAndUpdateAsync("cliente2", Rule);

        var decision = await store.GetAndUpdateAsync("cliente3", Rule);

        decision.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task CacheLleno_ClienteExistente_SigueFuncionando()
    {
        //creamos el propio store para no crear 500 clientes
        var opciones = new RateLimiterOptions
        {
            InMemory = new RateLimiterOptions.InMemorySettings { LockPoolSize = 4, MaxEntries = 2 }
        };
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 2 });
        var time  = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryBucketStore(cache, time, opciones);

        await store.GetAndUpdateAsync("cliente1", Rule);
        await store.GetAndUpdateAsync("cliente2", Rule);

        // cliente1 ya existe en el caché — debe seguir operando aunque el caché esté lleno
        var decision = await store.GetAndUpdateAsync("cliente1", Rule);

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Concurrencia_ClientesDistintos_NoSeInterbloquean()
    {
        var (store, _) = CreateStore();

        // Clientes distintos deben poder procesarse en paralelo sin deadlock
        var tasks   = Enumerable.Range(0, 10).Select(i => store.GetAndUpdateAsync($"cliente{i}", Rule));
        var results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r => r.Allowed.Should().BeTrue());
    }
}

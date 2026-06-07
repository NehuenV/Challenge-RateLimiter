using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using RateLimiter.Config;
using RateLimiter.Storage;

namespace RateLimiter.Tests;

public class ResilientBucketStoreTests
{
    private static readonly RateLimitRule Rule = new("test", capacity: 5, refillRate: 1.0);

    // valores bajos para hacer pruebas coherentes
    private static readonly RateLimiterOptions Opciones = new()
    {
        CircuitBreaker = new RateLimiterOptions.CircuitBreakerSettings
        {
            FailureRatio           = 0.5,
            SamplingDurationSeconds = 10,
            MinimumThroughput      = 5,
            BreakDurationSeconds   = 30
        }
    };

    [Fact]
    public async Task CuandoPrimaryFalla_UsaFallback()
    {
        //mock para simular errores y satisfactorio
        var primary  = new SiempreFallaBucketStore();
        var fallback = new SiemprePermiteBucketStore();
        var store    = new ResilientBucketStore(primary, fallback, Opciones, NullLogger<ResilientBucketStore>.Instance);

        var decision = await store.GetAndUpdateAsync("cliente1", Rule);

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task CuandoPrimaryFunciona_NousaFallback()
    {
        var primary  = new SiemprePermiteBucketStore();
        var fallback = new SiempreFallaBucketStore();
        var store    = new ResilientBucketStore(primary, fallback, Opciones, NullLogger<ResilientBucketStore>.Instance);

        var decision = await store.GetAndUpdateAsync("cliente1", Rule);
        //validacion de que coincidan las peticiones
        decision.Allowed.Should().BeTrue();
        primary.Llamadas.Should().Be(1);
    }

    [Fact]
    public async Task CircuitoSeAbre_DespuesDeMultiplesFallos_YUsaFallback()
    {
        var primary  = new SiempreFallaBucketStore();
        var fallback = new SiemprePermiteBucketStore();
        var store    = new ResilientBucketStore(primary, fallback, Opciones, NullLogger<ResilientBucketStore>.Instance);

        // enviamos 10 peticiones, 5 deberian fallar la primera vez e irse por el fallback
        var tareas = Enumerable.Range(0, 10).Select(_ => store.GetAndUpdateAsync("cliente1", Rule));
        //las otras 5 deberian pasar directo porque van al fallback que siempre permite
        var resultados = await Task.WhenAll(tareas);

        // al final las 10 deberian haber pasado de forma correcta
        resultados.Should().AllSatisfy(r => r.Allowed.Should().BeTrue());
    }

    [Fact]
    public async Task FalloTotal_ClientesConocidos_SiguenOperando_NuevosRechazados()
    {
        // Escenario: Redis caído + caché en memoria lleno por ataque de IPs rotativas.
        // los clientes conocidos deberian poder seguir haciendo peticiones
        // clientes nuevos rechazados
        var opcionesConMemoriaLimitada = new RateLimiterOptions
        {
            InMemory       = new RateLimiterOptions.InMemorySettings { LockPoolSize = 4, MaxEntries = 2 },
            CircuitBreaker = Opciones.CircuitBreaker
        };

        var cache       = new MemoryCache(new MemoryCacheOptions { SizeLimit = 2 });
        //configuramos esto para poder viajar en el tiempo
        var time        = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var memoriaFull = new InMemoryBucketStore(cache, time, opcionesConMemoriaLimitada);
        var primary     = new SiempreFallaConContador();
        var store       = new ResilientBucketStore(primary, memoriaFull, Opciones, NullLogger<ResilientBucketStore>.Instance);

        // llenamos el caché con dos clientes conocidos 
        await store.GetAndUpdateAsync("cliente-conocido-1", Rule);
        await store.GetAndUpdateAsync("cliente-conocido-2", Rule);

        // ya tenemos dos clientes, este es nuevo y no sabemos si es atacante o legitimo
        var nuevoCliente    = await store.GetAndUpdateAsync("cliente-nuevo", Rule);

        // este cliente ya lo conocemos y debe poder seguir operando
        var clienteConocido = await store.GetAndUpdateAsync("cliente-conocido-1", Rule);

        nuevoCliente.Allowed.Should().BeFalse();
        clienteConocido.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task CircuitoAbierto_PrimaryDejaDeSerLlamado()
    {
        var primary  = new SiempreFallaConContador();
        var fallback = new SiemprePermiteBucketStore();
        var store    = new ResilientBucketStore(primary, fallback, Opciones, NullLogger<ResilientBucketStore>.Instance);

        // lanzamos tantas peticiones como sea el minimo para abrir el circuito
        for (var i = 0; i < Opciones.CircuitBreaker.MinimumThroughput; i++)
            await store.GetAndUpdateAsync("cliente1", Rule);
        //guardamos el valor de las llamadas que se hicieron al primary
        var llamadasAlAbrirCircuito = primary.Llamadas;

        //con el circuito abierto deberian ir todas al fallback
        for (var i = 0; i < 5; i++)
            await store.GetAndUpdateAsync("cliente1", Rule);

        primary.Llamadas.Should().Be(llamadasAlAbrirCircuito);
    }

    // Stubs mínimos para controlar el comportamiento en tests
    private sealed class SiempreFallaBucketStore : IBucketStore
    {
        public Task<BucketDecision> GetAndUpdateAsync(string key, RateLimitRule rule) =>
            throw new InvalidOperationException("Store simulado fallando");
    }

    private sealed class SiempreFallaConContador : IBucketStore
    {
        public int Llamadas { get; private set; }

        public Task<BucketDecision> GetAndUpdateAsync(string key, RateLimitRule rule)
        {
            Llamadas++;
            throw new InvalidOperationException("Store simulado fallando");
        }
    }

    private sealed class SiemprePermiteBucketStore : IBucketStore
    {
        public int Llamadas { get; private set; }

        public Task<BucketDecision> GetAndUpdateAsync(string key, RateLimitRule rule)
        {
            Llamadas++;
            return Task.FromResult(new BucketDecision(true, 4, 0));
        }
    }
}

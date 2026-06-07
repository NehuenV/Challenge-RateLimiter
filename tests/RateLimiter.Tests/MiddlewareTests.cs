using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RateLimiter.Config;
using RateLimiter.Storage;

namespace RateLimiter.Tests;

public class MiddlewareTests : IClassFixture<WebApplicationFactory<Program>>
{
    //esto es posible gracias al partial del program
    private readonly WebApplicationFactory<Program> _factory;

    public MiddlewareTests(WebApplicationFactory<Program> factory)
    {
        // se reemplaza la configuración para usar reglas de test controladas
        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(new RateLimiterOptions
                {
                    Store = "Memory",
                    Rules =
                    [
                        new RateLimiterOptions.RuleEntry
                        {
                            PathPrefix = "/api/test",
                            Name       = "test-rule",
                            Capacity   = 3,
                            RefillRate = 1.0
                        }
                    ]
                });
            }));
    }

    [Fact]
    public async Task DentroDelLimite_Retorna200()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/test");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SuperandoElLimite_Retorna429()
    {
        var client = _factory.CreateClient();

        // Agotar el bucket (capacidad 3)
        for (var i = 0; i < 3; i++)
            await client.GetAsync("/api/test");

        var response = await client.GetAsync("/api/test");

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Respuesta429_IncluyeHeadersCorrectamente()
    {
        var client = _factory.CreateClient();

        for (var i = 0; i < 3; i++)
            await client.GetAsync("/api/test");

        var response = await client.GetAsync("/api/test");

        response.Headers.Should().ContainKey("X-RateLimit-Limit");
        response.Headers.Should().ContainKey("X-RateLimit-Remaining");
        response.Headers.Should().ContainKey("X-RateLimit-Retry-After");
        response.Headers.GetValues("X-RateLimit-Limit").First().Should().Be("3");
    }

    [Fact]
    public async Task Respuesta200_IncluyeHeadersDeTokensRestantes()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/test");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Should().ContainKey("X-RateLimit-Remaining");
        response.Headers.Should().ContainKey("X-RateLimit-Limit");
    }

    [Fact]
    public async Task RetryAfter_TieneValorPositivo()
    {
        var client = _factory.CreateClient();

        for (var i = 0; i < 3; i++)
            await client.GetAsync("/api/test");

        var response = await client.GetAsync("/api/test");

        var retryAfter = int.Parse(response.Headers.GetValues("X-RateLimit-Retry-After").First());
        retryAfter.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RetryAfter_SinJitter_EsExactamenteCeiling()
    {
        // jittermaxseconds en 0 evita variabilidad en el desbloque de clientes
        //especialmente util para test porque si una variacion de una fraccion de segundo podria cambiar el resultado
        var factory = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(services =>
                services.AddSingleton(new RateLimiterOptions
                {
                    Store            = "Memory",
                    JitterMaxSeconds = 0,
                    Rules            = [new RateLimiterOptions.RuleEntry
                    {
                        PathPrefix = "/api/test",
                        Name       = "test-rule",
                        Capacity   = 1,
                        RefillRate = 1.0
                    }]
                })));

        var client = factory.CreateClient();
        await client.GetAsync("/api/test"); // consume el unico token

        var response   = await client.GetAsync("/api/test");
        var retryAfter = int.Parse(response.Headers.GetValues("X-RateLimit-Retry-After").First());

        //al tener el jitter en 0 no agrego numeros a la cuenta y el redondeo(Ceiling) siempre daria 1
        retryAfter.Should().Be(1);
    }

    [Fact]
    public async Task RetryAfter_ConJitter_EstaEnElRangoEsperado()
    {
        // JitterMaxSeconds en 1 el valor puede estar entre 1 y 2( jitter+base) 
        var factory = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(services =>
                services.AddSingleton(new RateLimiterOptions
                {
                    Store            = "Memory",
                    JitterMaxSeconds = 1.0,
                    Rules            = [new RateLimiterOptions.RuleEntry
                    {
                        PathPrefix = "/api/test",
                        Name       = "test-rule",
                        Capacity   = 1,
                        RefillRate = 1.0
                    }]
                })));

        var client = factory.CreateClient();
        await client.GetAsync("/api/test");

        var response   = await client.GetAsync("/api/test");
        var retryAfter = int.Parse(response.Headers.GetValues("X-RateLimit-Retry-After").First());

        retryAfter.Should().BeGreaterThanOrEqualTo(1).And.BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task PathPrefixMasEspecifico_UsaSuPropiaRegla()
    {
        // Configura dos reglas con capacidades muy distintas para que el test sea determinista
        var factory = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(services =>
                services.AddSingleton(new RateLimiterOptions
                {
                    Store = "Memory",
                    Rules =
                    [
                        new RateLimiterOptions.RuleEntry
                        {
                            PathPrefix = "/api/test/strict",
                            Name       = "strict-rule",
                            Capacity   = 1,
                            RefillRate = 0.1
                        },
                        new RateLimiterOptions.RuleEntry
                        {
                            PathPrefix = "/api/test",
                            Name       = "default-rule",
                            Capacity   = 10,
                            RefillRate = 1.0
                        }
                    ]
                })));

        var client = factory.CreateClient();

        // /api/test/strict tiene capacidad 1, la segunda request deberia ser rechazada
        await client.GetAsync("/api/test/strict");
        var strictResponse = await client.GetAsync("/api/test/strict");

        // /api/test tiene capacidad 10, esta sin usar y debe pasar
        var defaultResponse = await client.GetAsync("/api/test");

        strictResponse.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        defaultResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RemainingTokens_DecrementaConCadaRequest()
    {
        var client = _factory.CreateClient();

        var r1 = await client.GetAsync("/api/test");
        var r2 = await client.GetAsync("/api/test");
        //deberia dar 2
        var remaining1 = double.Parse(r1.Headers.GetValues("X-RateLimit-Remaining").First());
        //deberia dar 1
        var remaining2 = double.Parse(r2.Headers.GetValues("X-RateLimit-Remaining").First());
        // 1<2
        remaining2.Should().BeLessThan(remaining1);
    }

    [Fact]
    public async Task EndpointSinRegla_PasaSinLimitar()
    {
        var client = _factory.CreateClient();

        // health no tiene regla configurada, nunca debe ser limitado
        for (var i = 0; i < 10; i++)
        {
            var response = await client.GetAsync("/health");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public void Construccion_ConPathPrefixDuplicado_LanzaExcepcion()
    {
        var opciones = new RateLimiterOptions
        {
            Rules =
            [
                new RateLimiterOptions.RuleEntry { PathPrefix = "/api", Name = "regla-a", Capacity = 5,  RefillRate = 1 },
                new RateLimiterOptions.RuleEntry { PathPrefix = "/api", Name = "regla-b", Capacity = 10, RefillRate = 2 }
            ]
        };
        //creamos una regla duplicada
        var act = () => new RateLimiter.Middleware.RateLimiterMiddleware(
            _ => Task.CompletedTask,
            new StoreStub(),
            new RateLimiter.KeyExtraction.RemoteIpKeyExtractor(),
            opciones,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RateLimiter.Middleware.RateLimiterMiddleware>.Instance);
        //validamos que falle para no tener un comportamiento inesperado
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*duplicado*");
    }

    private sealed class StoreStub : RateLimiter.Storage.IBucketStore
    {
        public Task<RateLimiter.Storage.BucketDecision> GetAndUpdateAsync(string key, RateLimiter.Config.RateLimitRule rule) =>
            Task.FromResult(new RateLimiter.Storage.BucketDecision(true, 5, 0));
    }
}

using Polly;
using Polly.CircuitBreaker;
using RateLimiter.Config;
using StackExchange.Redis;

namespace RateLimiter.Storage;

public sealed partial class ResilientBucketStore : IBucketStore
{
    private readonly IBucketStore _primary;
    private readonly IBucketStore _fallback;
    private readonly ResiliencePipeline _pipeline;
    private readonly ILogger<ResilientBucketStore> _logger;

    // Estados del circuit breaker:
    //   Cerrado      → todas las requests van a _primary (Redis).
    //   Abierto      → después de superar FailureRatio, las requests fallan
    //                  inmediatamente sin esperar el timeout de red de Redis.
    //                  Evita la cascada de lentitud durante una caída.
    //   Semi-abierto → después de BreakDuration, se deja pasar una request de prueba.
    //                  Si funciona, el circuito se cierra. Si falla, se reabre.
    public ResilientBucketStore(
        IBucketStore primary,
        IBucketStore fallback,
        RateLimiterOptions options,
        ILogger<ResilientBucketStore> logger)
    {
        _primary  = primary;
        _fallback = fallback;
        _logger   = logger;

        var cb = options.CircuitBreaker;

        _pipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio      = cb.FailureRatio,
                SamplingDuration  = TimeSpan.FromSeconds(cb.SamplingDurationSeconds),
                MinimumThroughput = cb.MinimumThroughput,
                BreakDuration     = TimeSpan.FromSeconds(cb.BreakDurationSeconds),
                OnOpened     = _ => { CircuitoAbierto(_logger);     return ValueTask.CompletedTask; },
                OnClosed     = _ => { CircuitoCerrado(_logger);     return ValueTask.CompletedTask; },
                OnHalfOpened = _ => { CircuitoSemiAbierto(_logger); return ValueTask.CompletedTask; }
            })
            .Build();
    }

    public async Task<BucketDecision> GetAndUpdateAsync(string key, RateLimitRule rule)
    {
        try
        {
            //ejecucion del camino faliz
            return await _pipeline.ExecuteAsync(async ct =>
                await _primary.GetAndUpdateAsync(key, rule), CancellationToken.None);
        }
        catch (Exception ex)
        {
            // El circuit breaker ya contó el fallo antes de re-lanzar.
            // Cualquier excepción del primary activa el fallback a memoria.
            FallbackActivado(_logger, key, ex);
            return await _fallback.GetAndUpdateAsync(key, rule);
        }
    }

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning,
        Message = "Circuito Redis abierto — usando store en memoria")]
    private static partial void CircuitoAbierto(ILogger logger);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information,
        Message = "Circuito Redis cerrado — Redis restaurado")]
    private static partial void CircuitoCerrado(ILogger logger);

    [LoggerMessage(EventId = 12, Level = LogLevel.Information,
        Message = "Circuito Redis semi-abierto — probando conexión")]
    private static partial void CircuitoSemiAbierto(ILogger logger);

    // El parámetro Exception se pasa como excepción del log entry (no como parte del mensaje).
    [LoggerMessage(EventId = 13, Level = LogLevel.Warning,
        Message = "Store primario no disponible para {Key} — usando fallback")]
    private static partial void FallbackActivado(ILogger logger, string key, Exception ex);
}

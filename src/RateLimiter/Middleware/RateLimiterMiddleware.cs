using RateLimiter.Config;
using RateLimiter.KeyExtraction;
using RateLimiter.Metrics;
using RateLimiter.Storage;

namespace RateLimiter.Middleware;

public sealed partial class RateLimiterMiddleware
{
    private readonly RequestDelegate    _next;
    private readonly IBucketStore       _store;
    private readonly IKeyExtractor      _keyExtractor;
    private readonly double             _jitterMaxSeconds;
    private readonly RateLimiterMetrics _metrics;
    private readonly ILogger<RateLimiterMiddleware> _logger;

    // Las reglas se ordenan por longitud de PathPrefix descendente para que la
    // más específica gane. Ej: "/api/test/strict" se evalúa antes que "/api/test".
    private readonly IReadOnlyList<(string PathPrefix, RateLimitRule Rule)> _rules;

    public RateLimiterMiddleware(
        RequestDelegate next,
        IBucketStore store,
        IKeyExtractor keyExtractor,
        RateLimiterOptions options,
        RateLimiterMetrics metrics,
        ILogger<RateLimiterMiddleware> logger)
    {
        _next             = next;
        _store            = store;
        _keyExtractor     = keyExtractor;
        _jitterMaxSeconds = options.JitterMaxSeconds;
        _metrics          = metrics;
        _logger           = logger;

        // Falla en el arranque si hay PathPrefixes duplicados.
        // Un duplicado silencioso haría que una regla sea ignorada sin ningún aviso,
        // lo que puede dejar endpoints sin protección o con la regla equivocada.
        var duplicados = options.Rules
            .GroupBy(r => r.PathPrefix)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicados.Count > 0)
            throw new InvalidOperationException(
                $"PathPrefix duplicado en la configuración de reglas: {string.Join(", ", duplicados)}");

        _rules = options.Rules
            .OrderByDescending(r => r.PathPrefix.Length)
            .Select(r => (r.PathPrefix, r.ToRule()))
            .ToList();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var match = _rules.FirstOrDefault(r =>
            context.Request.Path.StartsWithSegments(r.PathPrefix));

        if (match == default)
        {
            await _next(context);
            return;
        }

        var rule      = match.Rule;
        var clientKey = _keyExtractor.Extract(context);

        // La clave del bucket combina identidad del cliente y nombre de regla para que
        // cada cliente tenga buckets independientes por endpoint. Consumir el límite
        // de /strict no drena el bucket de /test del mismo cliente.
        var bucketKey = $"{clientKey}:{rule.Name}";
        var decision  = await _store.GetAndUpdateAsync(bucketKey, rule);

        context.Response.Headers["X-RateLimit-Limit"]     = rule.Capacity.ToString("F0");
        context.Response.Headers["X-RateLimit-Remaining"] = decision.RemainingTokens.ToString("F2");

        if (!decision.Allowed)
        {
            // El jitter distribuye los reintentos en una ventana aleatoria para evitar
            // que todos los clientes bloqueados simultáneamente vuelvan al mismo segundo
            // (Thundering Herd). No miente al cliente: el valor siempre es >= el tiempo real.
            var jitter     = _jitterMaxSeconds > 0 ? Random.Shared.NextDouble() * _jitterMaxSeconds : 0;
            var retryAfter = (int) Math.Ceiling(decision.RetryAfterSeconds + jitter);
            context.Response.Headers["X-RateLimit-Retry-After"] = retryAfter.ToString();
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            LimiteSuperado(_logger, clientKey, rule.Name, retryAfter);
            _metrics.RequestDenegado(rule.Name);
            return;
        }

        var remaining = Math.Round(decision.RemainingTokens, 2);
        RequestPermitido(_logger, clientKey, rule.Name, remaining);
        _metrics.RequestPermitido(rule.Name, remaining);
        await _next(context);
    }

    // LoggerMessageAttribute genera el método en compile time:
    // - verifica si el nivel está habilitado antes de allocar nada
    // - no boxea value types ni crea params object[]
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Límite superado — ClientKey={ClientKey} Regla={Rule} ReintentarEn={RetryAfter}s")]
    private static partial void LimiteSuperado(ILogger logger, string clientKey, string rule, int retryAfter);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Request permitido — ClientKey={ClientKey} Regla={Rule} TokensRestantes={Remaining}")]
    private static partial void RequestPermitido(ILogger logger, string clientKey, string rule, double remaining);
}

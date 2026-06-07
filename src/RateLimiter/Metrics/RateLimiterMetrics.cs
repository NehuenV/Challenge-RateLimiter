using System.Diagnostics.Metrics;

namespace RateLimiter.Metrics;

/// <summary>
/// Instrumentos de métricas del rate limiter usando System.Diagnostics.Metrics (.NET nativo).
/// OpenTelemetry actúa como listener y los expone en /metrics para Prometheus.
/// </summary>
public sealed class RateLimiterMetrics
{
    // Nombre del Meter — debe coincidir con el registrado en AddMeter() de Program.cs.
    public const string MeterName = "RateLimiter";

    private readonly Counter<long>     _requests;
    private readonly Histogram<double> _tokensRemaining;

    public RateLimiterMetrics(IMeterFactory meterFactory)
    {
        // IMeterFactory es inyectado por el DI de .NET — gestiona el ciclo de vida del Meter
        // y lo conecta automáticamente al MeterProvider de OpenTelemetry.
        var meter = meterFactory.Create(MeterName);

        // Un único counter con etiquetas rule + result es más eficiente que dos counters separados.
        // Permite queries como: rate(rate_limiter_requests_total{result="denied"}[5m])
        _requests = meter.CreateCounter<long>(
            name:        "rate_limiter.requests",
            unit:        "requests",
            description: "Total de requests procesados por el rate limiter");

        // Distribución de tokens restantes — útil para detectar reglas demasiado ajustadas
        // (histograma sesgado hacia 0) o reglas sobredimensionadas (sesgado hacia Capacity).
        _tokensRemaining = meter.CreateHistogram<double>(
            name:        "rate_limiter.tokens_remaining",
            unit:        "tokens",
            description: "Tokens disponibles en el bucket en el momento del request");
    }

    public void RequestPermitido(string rule, double tokensRestantes)
    {
        _requests.Add(1,
            new KeyValuePair<string, object?>("rule",   rule),
            new KeyValuePair<string, object?>("result", "allowed"));

        _tokensRemaining.Record(tokensRestantes,
            new KeyValuePair<string, object?>("rule", rule));
    }

    public void RequestDenegado(string rule)
    {
        _requests.Add(1,
            new KeyValuePair<string, object?>("rule",   rule),
            new KeyValuePair<string, object?>("result", "denied"));
    }
}

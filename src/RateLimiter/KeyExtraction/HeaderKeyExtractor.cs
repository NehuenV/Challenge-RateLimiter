namespace RateLimiter.KeyExtraction;

// Extrae la identidad del cliente desde un header HTTP (ej: X-Api-Key, X-User-Id).
// Útil cuando el caller es un servicio conocido que se identifica por header,
// o para testing de rate limiting desde una misma IP.
public sealed class HeaderKeyExtractor : IKeyExtractor
{
    private readonly string _headerName;

    public HeaderKeyExtractor(string headerName) => _headerName = headerName;

    public string Extract(HttpContext context) =>
        context.Request.Headers[_headerName].FirstOrDefault() ?? "anonymous";
}

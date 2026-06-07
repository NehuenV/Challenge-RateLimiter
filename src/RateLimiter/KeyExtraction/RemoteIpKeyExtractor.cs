namespace RateLimiter.KeyExtraction;

// Extractor por defecto — usa la IP de la conexión TCP directa.
// En producción detrás de un load balancer, combinar con ForwardedHeadersMiddleware
// para que RemoteIpAddress refleje la IP real del cliente y no la del LB.
public sealed class RemoteIpKeyExtractor : IKeyExtractor
{
    public string Extract(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

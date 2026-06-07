using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace RateLimiter.Swagger;

/// <summary>
/// Agrega automáticamente los headers X-RateLimit-* y la respuesta 429
/// a todos los endpoints documentados en Swagger.
/// </summary>
public sealed class RateLimitHeadersFilter : IOperationFilter
{
    private static readonly Dictionary<string, OpenApiHeader> RateLimitHeaders = new()
    {
        ["X-RateLimit-Limit"]     = new() { Description = "Capacidad máxima del bucket", Schema = new() { Type = "integer" } },
        ["X-RateLimit-Remaining"] = new() { Description = "Tokens restantes en la ventana actual", Schema = new() { Type = "number" } }
    };

    private static readonly OpenApiResponse TooManyRequestsResponse = new()
    {
        Description = "Too Many Requests — límite de tasa superado",
        Headers = new Dictionary<string, OpenApiHeader>(RateLimitHeaders)
        {
            ["X-RateLimit-Retry-After"] = new() { Description = "Segundos hasta poder reintentar (incluye jitter)", Schema = new() { Type = "integer" } }
        }
    };

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        // Agrega los headers de rate limit a las respuestas exitosas
        foreach (var response in operation.Responses.Values)
        {
            response.Headers ??= new Dictionary<string, OpenApiHeader>();
            foreach (var (key, header) in RateLimitHeaders)
                response.Headers.TryAdd(key, header);
        }

        // Agrega la respuesta 429 con sus headers específicos
        operation.Responses.TryAdd("429", TooManyRequestsResponse);
    }
}

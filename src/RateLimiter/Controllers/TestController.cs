using Microsoft.AspNetCore.Mvc;

namespace RateLimiter.Controllers;

/// <summary>
/// Endpoints de demostración para probar el rate limiting.
/// </summary>
[ApiController]
[Route("api/test")]
[Produces("application/json")]
public sealed class TestController : ControllerBase
{
    /// <summary>
    /// Endpoint con regla estándar (10 tokens, recarga 1/seg).
    /// </summary>
    /// <remarks>
    /// Consume 1 token por request. Cuando el bucket se agota devuelve 429
    /// con los headers X-RateLimit-* para que el cliente sepa cuándo reintentar.
    /// </remarks>
    /// <response code="200">Request permitido dentro del límite</response>
    /// <response code="429">Límite superado — ver header Retry-After</response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public IActionResult Get() =>
        Ok(new { message = "Request permitido", timestamp = DateTime.UtcNow });

    /// <summary>
    /// Endpoint con regla estricta (3 tokens, recarga 0.5/seg).
    /// </summary>
    /// <remarks>
    /// Misma lógica que /api/test pero con un bucket independiente y más pequeño.
    /// Útil para demostrar que cada regla mantiene estado separado por cliente.
    /// </remarks>
    /// <response code="200">Request permitido dentro del límite</response>
    /// <response code="429">Límite superado — ver header Retry-After</response>
    [HttpGet("strict")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public IActionResult GetStrict() =>
        Ok(new { message = "Strict endpoint — request permitido", timestamp = DateTime.UtcNow });
}

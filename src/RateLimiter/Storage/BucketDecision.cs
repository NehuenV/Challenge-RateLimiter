namespace RateLimiter.Storage;

// Lo que IBucketStore devuelve al middleware.
// No expone NewState — la persistencia es un detalle interno de cada implementación.
public record BucketDecision(
    bool   Allowed,
    double RemainingTokens,
    double RetryAfterSeconds);

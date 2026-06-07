namespace RateLimiter.Config;

public record RateLimitRule(
    string Name,
    double Capacity,
    double RefillRate,
    string? Description = null);

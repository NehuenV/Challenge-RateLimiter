namespace RateLimiter.Config;

public record RateLimitRule
{
    public string  Name        { get; init; }
    public double  Capacity    { get; init; }
    public double  RefillRate  { get; init; }
    public string? Description { get; init; }

    public RateLimitRule(string name, double capacity, double refillRate, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("El nombre de la regla no puede estar vacío.", nameof(name));

        // Capacity = 0 genera un bucket que nunca permite requests.
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity),
                $"Capacity debe ser > 0 en la regla '{name}'.");

        // RefillRate = 0 causa división por cero en el cálculo de RetryAfterSeconds
        // y en el TTL de expiración del caché.
        if (refillRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(refillRate),
                $"RefillRate debe ser > 0 en la regla '{name}'. Un valor de 0 causa división por cero.");

        Name        = name;
        Capacity    = capacity;
        RefillRate  = refillRate;
        Description = description;
    }
}

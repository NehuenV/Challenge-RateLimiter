namespace RateLimiter.Config;

public sealed class RateLimiterOptions
{
    public string Store { get; set; } = "Memory";

    // connectTimeout and syncTimeout (ms) protect against slow/unresponsive Redis.
    // Without these, a hung Redis connection blocks the thread indefinitely.
    public string RedisConnection { get; set; } = "localhost:6379,connectTimeout=1000,syncTimeout=500";

    // Segundos de offset aleatorio que se suman al Retry-After devuelto al cliente.
    // Rompe la sincronización cuando muchos clientes son bloqueados al mismo tiempo
    // (Thundering Herd): en lugar de que todos reintenten exactamente al mismo segundo,
    // los intentos se distribuyen en una ventana de JitterMaxSeconds.
    // Poner en 0 desactiva el jitter (útil en tests para valores deterministas).
    public double JitterMaxSeconds { get; set; } = 1.0;

    public CircuitBreakerSettings CircuitBreaker { get; set; } = new();

    public InMemorySettings InMemory { get; set; } = new();

    public List<RuleEntry> Rules { get; set; } = new();

    public sealed class InMemorySettings
    {
        // Cantidad de slots en el pool de semáforos (striped locking).
        // Más slots = menos contención entre clientes distintos que caen en el mismo slot.
        // Se recomienda potencia de 2 para distribución uniforme del hash (%, 64 → 0..63).
        public int LockPoolSize { get; set; } = 64;

        // Máximo de entradas simultáneas en el caché en memoria.
        // Clientes nuevos son rechazados (fail-closed) cuando se alcanza este límite.
        // Ajustar según la RAM disponible: cada entrada ocupa ~200 bytes (key + State).
        public int MaxEntries { get; set; } = 100_000;
    }

    public sealed class CircuitBreakerSettings
    {
        // Porcentaje de fallos (0.0 a 1.0) necesarios para abrir el circuito,
        // evaluado sobre la ventana de SamplingDurationSeconds.
        public double FailureRatio { get; set; } = 0.5;

        // Duración de la ventana de muestreo en segundos.
        public int SamplingDurationSeconds { get; set; } = 10;

        // Mínimo de llamadas en la ventana antes de evaluar el FailureRatio.
        // Evita abrir el circuito por un solo fallo al inicio.
        public int MinimumThroughput { get; set; } = 5;

        // Tiempo en segundos que el circuito permanece abierto antes de pasar a semi-abierto.
        public int BreakDurationSeconds { get; set; } = 30;
    }

    public sealed class RuleEntry
    {
        public string PathPrefix  { get; set; } = "/";
        public string Name        { get; set; } = "";
        public double Capacity    { get; set; }
        public double RefillRate  { get; set; }
        public string? Description { get; set; }

        public RateLimitRule ToRule() => new(Name, Capacity, RefillRate, Description);
    }
}

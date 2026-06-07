namespace RateLimiter.Algorithm;

public static class TokenBucket
{
    public record State(double Tokens, long LastRefillAt);

    public record Decision(
        bool   Allowed,
        State  NewState,
        double RemainingTokens,
        double RetryAfterSeconds);

    // evita rechazos por decimales cercanos al 1.0.
    // 0.9999999999999998 tokens debe contar como 1 token disponible.
    private const double Threshold = 1.0 - 1e-9;

    public static Decision Evaluate(State current, Config.RateLimitRule rule, long nowMs)
    {
        // Se fuerza a cero para protegerse contra tiempo negativo por clock skew
        // entre instancias distribuidas (el reloj de una instancia por detrás de otra).
        var elapsed  = Math.Max(0, (nowMs - current.LastRefillAt) / 1000.0);
        var refilled = Math.Min(rule.Capacity, current.Tokens + elapsed * rule.RefillRate);

        if (refilled < Threshold)
        {
            var retryAfter = (1.0 - refilled) / rule.RefillRate;
            return new Decision(
                Allowed:           false,
                NewState:          current with { Tokens = refilled, LastRefillAt = nowMs },
                RemainingTokens:   refilled,
                RetryAfterSeconds: retryAfter);
        }

        return new Decision(
            Allowed:           true,
            NewState:          new State(refilled - 1, nowMs),
            RemainingTokens:   refilled - 1,
            RetryAfterSeconds: 0);
    }
}

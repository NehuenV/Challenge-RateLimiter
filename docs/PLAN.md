# Rate Limiter — Plan de Desarrollo

## Contexto

Challenge de entrevista técnica basado en el Capítulo 4 de "System Design Interview" (Alex Xu).
Implementar un **Rate Limiter funcional, testeado y bien diseñado** en C#.

---

## Decisiones de Diseño

### Algoritmo: Token Bucket con Lazy Refill

No se usan timers ni workers en background. El estado mínimo por cliente es:

```
{ tokens: double, last_refill_at: long (ms) }
```

Cuando llega una solicitud:
1. Calcular tiempo transcurrido desde `last_refill_at`
2. Derivar tokens actuales: `min(capacity, stored_tokens + elapsed_seconds * refill_rate)`
3. Si tokens >= 1 → permitir, decrementar, guardar nuevo estado
4. Si no → rechazar, guardar estado con tokens recalculados

Los clientes inactivos no consumen recursos. En Redis, el key expira con TTL automático.

**Por qué Token Bucket y no los otros:**
- Sliding Window Log: más preciso pero alto consumo de memoria (guarda todos los timestamps)
- Sliding Window Counter: más simple pero es una aproximación (asume distribución uniforme)
- Fixed Window Counter: bug conocido en los bordes de ventana
- Leaking Bucket: no maneja ráfagas bien, queue management agrega complejidad

### Atomicidad

| Store | Mecanismo |
|---|---|
| Redis | Script Lua (el read-compute-write es una operación atómica, Redis es single-threaded en Lua) |
| Memoria | Pool fijo de `SemaphoreSlim` (striped locking) + `IMemoryCache` con sliding expiration |

**Sobre el Lua:** el script se escribe en sintaxis Lua y se embebe como string constante en C#.
StackExchange.Redis lo envía a Redis vía `ScriptEvaluateAsync`. Redis no puede retornar floats
desde Lua, por lo que se multiplican por 1000 antes del return y se dividen en C#.

### Store bifurcado (Redis / Memoria)

Configurado vía `appsettings.json`. El evaluador puede correr sin Docker.
Si tiene Docker, `docker-compose up` habilita el modo distribuido con Redis.

```json
{
  "RateLimiter": {
    "Store": "Memory"
  }
}
```

```csharp
// Program.cs
if (config["RateLimiter:Store"] == "Redis")
{
    services.AddSingleton<RedisBucketStore>();
    services.AddSingleton<InMemoryBucketStore>();   // actúa como fallback
    services.AddSingleton<IBucketStore, ResilientBucketStore>(); // circuit breaker encima de Redis
}
else
{
    services.AddSingleton<IBucketStore, InMemoryBucketStore>();
}
```

### Formato: ASP.NET Core Web API (no librería)

Una librería sin host no es demostrable. El middleware de ASP.NET Core es exactamente
el patrón que el libro describe. La separación interna es tan limpia que extraerla a
librería sería trivial si se requiriera.

---

## Estructura del Proyecto

```
rate-limiter/
├── README.md                               ← instrucciones para correr y testear
├── DESIGN.md                               ← decisiones arquitectónicas y uso de IA
├── docs/
│   ├── PLAN.md                             ← este archivo — proceso de diseño previo al código
│   └── EDGE_CASES.md                       ← análisis de edge cases y decisiones no obvias
├── docker-compose.yml                      ← solo Redis (dev local)
├── docker-compose.full.yml                 ← Redis + API (despliegue completo)
├── src/
│   └── RateLimiter/
│       ├── Algorithm/
│       │   └── TokenBucket.cs              ← función pura, sin dependencias externas
│       ├── Config/
│       │   ├── RateLimitRule.cs
│       │   └── RateLimiterOptions.cs       ← InMemory, CircuitBreaker, Jitter, Rules
│       ├── Storage/
│       │   ├── IBucketStore.cs
│       │   ├── BucketDecision.cs
│       │   ├── InMemoryBucketStore.cs
│       │   ├── RedisBucketStore.cs
│       │   └── ResilientBucketStore.cs     ← circuit breaker + fallback a memoria (Polly)
│       ├── Middleware/
│       │   └── RateLimiterMiddleware.cs    ← valida PathPrefixes duplicados al arrancar
│       ├── KeyExtraction/
│       │   ├── IKeyExtractor.cs
│       │   ├── RemoteIpKeyExtractor.cs     ← default
│       │   └── HeaderKeyExtractor.cs
│       ├── Metrics/
│       │   └── RateLimiterMetrics.cs      ← Counter + Histogram via System.Diagnostics.Metrics
│       ├── Swagger/
│       │   └── RateLimitHeadersFilter.cs  ← agrega headers X-RateLimit-* a todos los endpoints
│       ├── Controllers/
│       │   └── TestController.cs
│       ├── Program.cs
│       ├── appsettings.json
│       └── RateLimiter.http               ← requests de demo para VS / Rider / VS Code
├── tests/
│   └── RateLimiter.Tests/
│       ├── TokenBucketTests.cs            ← 10 tests del algoritmo puro
│       ├── InMemoryBucketStoreTests.cs    ← 8 tests incluyendo MaxEntries y concurrencia
│       ├── ResilientBucketStoreTests.cs   ← 5 tests incluyendo fallo total
│       └── MiddlewareTests.cs             ← 11 tests E2E con WebApplicationFactory
├── docker-compose.yml
└── DESIGN.md
```

Un solution, dos proyectos. Nada más.

---

## Contratos Clave

### `TokenBucket.cs` — Núcleo del algoritmo

```csharp
public static class TokenBucket
{
    public record State(double Tokens, long LastRefillAt);
    public record Decision(bool Allowed, State NewState, double RemainingTokens, double RetryAfterSeconds);

    public static Decision Evaluate(State current, RateLimitRule rule, long nowMs)
    {
        var elapsed  = Math.Max(0, (nowMs - current.LastRefillAt) / 1000.0);
        var refilled = Math.Min(rule.Capacity, current.Tokens + elapsed * rule.RefillRate);

        const double threshold = 1.0 - 1e-9;   // guard against floating point drift
        if (refilled < threshold)
        {
            var retryAfter = (1.0 - refilled) / rule.RefillRate;
            return new Decision(false, current with { Tokens = refilled, LastRefillAt = nowMs }, refilled, retryAfter);
        }

        return new Decision(true, new State(refilled - 1, nowMs), refilled - 1, 0);
    }
}
```

Sin Redis, sin `DateTime.Now`, sin efectos secundarios. 100% testeable de forma determinista.

**Cálculo de `RetryAfterSeconds`:** cuando se rechaza, sabemos exactamente cuántos tokens
hay y a qué tasa se recargan. `(1.0 - refilled) / refillRate` da los segundos exactos
hasta tener 1 token disponible. El header se emite con `Math.Ceiling()` para no mentirle
al cliente con un valor menor al real.

### `IBucketStore.cs`

```csharp
public interface IBucketStore
{
    // Atomic: lee el estado actual, evalúa contra la regla y guarda el resultado.
    // Cada implementación resuelve el tiempo internamente:
    //   InMemoryBucketStore → TimeProvider inyectable (testeable con FakeTimeProvider)
    //   RedisBucketStore    → redis.call('TIME') dentro del script Lua
    Task<Decision> GetAndUpdateAsync(string key, RateLimitRule rule);
}
```

`nowMs` no forma parte de la interfaz porque las dos implementaciones lo obtienen
de fuentes distintas. Sacarlo evita que `RedisBucketStore` reciba un parámetro que
ignora completamente. `TokenBucket.Evaluate` sigue recibiendo `nowMs` — cada store
lo obtiene a su manera y se lo pasa a la función pura.

### `RateLimitRule.cs`

```csharp
public record RateLimitRule(
    string Name,          // identificador único de la regla (se usa como parte del bucket key)
    double Capacity,      // máximo de tokens en el balde
    double RefillRate,    // tokens por segundo
    string? Description = null
);
```

### `IKeyExtractor.cs` — Identificación del cliente

Determina qué string identifica a un cliente dentro de un bucket. Desacoplado del
middleware para poder cambiar la estrategia sin tocar la lógica de rate limiting.

```csharp
public interface IKeyExtractor
{
    string Extract(HttpContext context);
}
```

Implementaciones incluidas:
- `RemoteIpKeyExtractor` — default. Usa `context.Connection.RemoteIpAddress`.
- `HeaderKeyExtractor` — extrae de un header configurable (ej: `X-Api-Key`, `X-Forwarded-For`).

La key final del bucket combina cliente + regla para que cada cliente tenga
buckets independientes por endpoint:

```
bucketKey = $"{clientKey}:{rule.Name}"
// ej: "203.0.113.5:strict-rule"
// ej: "203.0.113.5:default-rule"
```

Esto permite que consumir el límite de un endpoint no afecte al de otro.
Si en entrevista preguntan "¿y por usuario autenticado?", es agregar un
`UserIdKeyExtractor` que lee un claim, sin tocar el middleware ni el algoritmo.

### `ResilientBucketStore.cs` — Circuit Breaker con Polly

Decorator sobre `RedisBucketStore`. Cuando el circuito está abierto o Redis lanza
una excepción, cae transparentemente a `InMemoryBucketStore`.

```csharp
public class ResilientBucketStore : IBucketStore
{
    private readonly RedisBucketStore _redis;
    private readonly InMemoryBucketStore _memory;
    private readonly ResiliencePipeline _pipeline;
    private readonly ILogger<ResilientBucketStore> _logger;

    public ResilientBucketStore(
        RedisBucketStore redis,
        InMemoryBucketStore memory,
        ILogger<ResilientBucketStore> logger)
    {
        _redis   = redis;
        _memory  = memory;
        _logger  = logger;
        _pipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio     = 0.5,              // abre si el 50% de calls fallan
                SamplingDuration = TimeSpan.FromSeconds(10),
                MinimumThroughput = 5,               // mínimo de calls antes de evaluar
                BreakDuration    = TimeSpan.FromSeconds(30),
                OnOpened  = _ => { logger.LogWarning("Redis circuit opened — using memory fallback"); return ValueTask.CompletedTask; },
                OnClosed  = _ => { logger.LogInformation("Redis circuit closed — Redis restored");    return ValueTask.CompletedTask; },
            })
            .Build();
    }

    public async Task<Decision> GetAndUpdateAsync(string key, RateLimitRule rule)
    {
        try
        {
            return await _pipeline.ExecuteAsync(async ct =>
                await _redis.GetAndUpdateAsync(key, rule));
        }
        catch (Exception ex)  // BrokenCircuitException o fallo de Redis
        {
            _logger.LogWarning(ex, "Redis unavailable for key {Key} — falling back to memory", key);
            return await _memory.GetAndUpdateAsync(key, rule);
        }
    }
}
```

**Comportamiento:**
- Circuito **cerrado**: todas las requests van a Redis.
- Circuito **abierto** (>50% fallos en 10s con mínimo 5 calls): `BrokenCircuitException`
  inmediata → fallback a memoria sin esperar timeout de red.
- Circuito **semi-abierto**: después de 30s, deja pasar una request de prueba.
  Si funciona, cierra. Si falla, reabre.

El fallback a memoria no es distribuido — cada instancia tiene su propio estado.
Esto es degradación aceptable, documentada en `DESIGN.md`.

### `RateLimiterMiddleware.cs` — Headers de respuesta

Cuando se rechaza una solicitud (`429 Too Many Requests`):

| Header | Descripción |
|---|---|
| `X-RateLimit-Limit` | Capacidad máxima del bucket |
| `X-RateLimit-Remaining` | Tokens restantes |
| `X-RateLimit-Retry-After` | Segundos hasta tener 1 token disponible |

---

## Endpoints Demo

```
GET /api/test           ← limitado por IP (ej: 10 req / 10s)
GET /api/test/strict    ← regla más estricta (ej: 3 req / 10s)
GET /health             ← sin rate limiting
```

Suficiente para demostrar el comportamiento con curl o Postman.

---

## Plan de Implementación

### Fase 1 — Núcleo del algoritmo (sin infraestructura)
- [ ] `RateLimitRule.cs`
- [ ] `TokenBucket.State` y `TokenBucket.Decision`
- [ ] `TokenBucket.Evaluate()` — la función pura
- [ ] Tests unitarios de `TokenBucket` (deterministas, sin mocks)

### Fase 2 — Store en memoria
- [ ] `IBucketStore.cs`
- [ ] `InMemoryBucketStore` con striped locking (pool fijo de 64 `SemaphoreSlim`) + `IMemoryCache`
- [ ] `TimeProvider` inyectado en `InMemoryBucketStore` (permite tests sin `Thread.Sleep`)
- [ ] Tests de integración del store en memoria (incluyendo concurrencia)

### Fase 3 — Middleware y API
- [ ] `RateLimiterMiddleware` con lógica de key (IP del cliente)
- [ ] Registro en `Program.cs` con DI
- [ ] Endpoints demo
- [ ] Tests del middleware (usando `WebApplicationFactory`)

### Fase 4 — Store en Redis + Circuit Breaker
- [ ] Script Lua para operación atómica
- [ ] `RedisBucketStore` con StackExchange.Redis
- [ ] `ResilientBucketStore` con Polly — wrappea Redis con circuit breaker, cae a memoria si Redis falla
- [ ] `docker-compose.yml`
- [ ] Tests de integración Redis (skipeables si no hay Redis disponible)
- [ ] Tests del circuit breaker: verificar que al fallar Redis se activa el fallback

**Package:** `Microsoft.Extensions.Resilience` (Polly v8 integrado con el DI de .NET)

### Fase 5 — Pulido final ✅
- [x] Serilog con output JSON estructurado (`LoggerMessageAttribute` en clases críticas para zero GC pressure)
- [x] Timeouts de Redis en el connection string (`connectTimeout=1000,syncTimeout=500`)
- [x] Jitter configurable en `Retry-After` (`JitterMaxSeconds`) para mitigar Thundering Herd
- [x] Validación de PathPrefixes duplicados en el constructor del middleware (fail-fast al arrancar)
- [x] Swagger UI en `/swagger` con `RateLimitHeadersFilter` (documenta 429 y headers automáticamente)
- [x] `RateLimiter.http` con requests de demo para VS / Rider / VS Code
- [x] `DESIGN.md` con decisiones arquitectónicas, trade-offs y uso de IA
- [x] `README.md` con instrucciones completas de ejecución y configuración

**Nota sobre métricas:** los logs estructurados de Serilog con propiedades como `{Rule}`, `{ClientKey}`
y `{Remaining}` cubren el requerimiento — son queryables en cualquier sistema de agregación de logs.

---

## Estrategia de Tests

| Test | Tipo | Requiere infraestructura |
|---|---|---|
| `TokenBucket.Evaluate` — casos límite y NewState | Unitario | No |
| `TokenBucket.Evaluate` — refill, cap, clock skew, float | Unitario | No |
| `InMemoryBucketStore` — concurrencia, MaxEntries, expiración | Integración | No |
| `ResilientBucketStore` — fallback, circuito abierto, fallo total | Integración | No |
| `RateLimiterMiddleware` — 429, headers, jitter, PathPrefix, duplicados | Integración (WebApplicationFactory) | No |
| `RedisBucketStore` — operación atómica | Integración | Sí (Docker) |

**Total: 37 tests — todos pasan sin Docker**

Los tests de Redis se marcan con `[Trait("Category", "Redis")]` y se pueden excluir
con `dotnet test --filter "Category!=Redis"`.

---

## Consideraciones de Concurrencia

- El algoritmo (`TokenBucket.Evaluate`) es una función pura — thread-safe por definición.
- `InMemoryBucketStore`: striped locking con pool fijo de 64 `SemaphoreSlim(1,1)`.
  La key se hashea para asignarle un slot: `Math.Abs(key.GetHashCode()) % 64`.
  Esto evita crear un lock por cada IP que llegue (memory leak con IPs rotativas).
  Los estados de bucket se guardan en `IMemoryCache` con `NeverRemove` + sliding expiration.
  El tiempo se obtiene via `TimeProvider` inyectado — en tests se usa `FakeTimeProvider`
  para controlar el reloj sin `Thread.Sleep`.
- `RedisBucketStore`: el script Lua garantiza atomicidad. No usar WATCH/MULTI/EXEC
  (requiere reintentos, más complejo). Redis maneja el TTL del key con `PEXPIRE`.
- No optimizar prematuramente: 64 slots es suficiente para el challenge.

---

## Fail-open vs Fail-closed

La política varía según el escenario. No es una decisión única:

**Redis completamente caído → fail-open para todos.**
El rate limiter no tiene información de ningún cliente. Rechazar todo castigaría
a usuarios legítimos por una falla de infraestructura. Se loguea el error y se
deja pasar con protección degradada.

**Cache en memoria lleno bajo ataque → fail-closed solo para clientes nuevos.**
El rate limiter sigue funcionando. Clientes con estado existente operan normal.
Clientes sin estado previo (actores desconocidos) son rechazados hasta que haya
capacidad. Los buckets existentes tienen `CacheItemPriority.NeverRemove` para
que no sean desalojados en favor de nuevos actores desconocidos.

La variable que determina la política es: *¿el sistema tiene información del cliente?*
- Sí → opera normalmente con esa información
- No y es por falla de infra → fail-open
- No y es por saturación → fail-closed para ese actor desconocido

---

## Qué NO hacer

- No implementar múltiples algoritmos (solo Token Bucket)
- No crear un proyecto de librería NuGet separado
- No agregar autenticación, routing de reglas complejo ni admin endpoints
- No escribir comentarios que expliquen QUÉ hace el código (los nombres lo dicen)
- No usar `DateTime.Now` dentro del algoritmo (dificulta los tests)
- No poner `Thread.Sleep` en los tests — controlar el tiempo via parámetro `nowMs`

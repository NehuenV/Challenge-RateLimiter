# DESIGN.md — Rate Limiter

## Por qué este problema

Elegí el Rate Limiter del Capítulo 4 porque tengo experiencia directa con el escenario
que motiva su existencia: en mi trabajo actual un cliente construyó un bot que golpeaba
nuestro servidor de tarifas en horario fijo, con suficiente volumen para tumbarlo
diariamente. Implementar esto no fue solo un ejercicio académico sino algo que me interesa
entender a fondo.

---

## Algoritmo: Token Bucket con Lazy Refill

### Por qué Token Bucket

Analicé los cinco algoritmos del libro:

| Algoritmo | Razón de descarte |
|---|---|
| Fixed Window Counter | Bug conocido en los bordes de ventana: permite el doble de requests esperadas |
| Leaking Bucket | No maneja ráfagas legítimas; la cola FIFO agrega complejidad sin beneficio claro |
| Sliding Window Log | Precisión perfecta pero almacena todos los timestamps incluso de requests rechazados |
| Sliding Window Counter | Aproximación (asume distribución uniforme); útil pero menos honesto para un prototipo |
| **Token Bucket** | Preciso, simple, maneja bursts, usado por Amazon y Stripe en producción |

Token Bucket con **lazy refill** fue la elección: en lugar de un worker que recarga fichas
periódicamente, se almacena solo `{ tokens, last_refill_at }` y se calcula cuántos tokens
debería haber al momento de cada request. Los clientes inactivos no consumen recursos y en
Redis los keys expiran solos con TTL.

### El núcleo es una función pura

`TokenBucket.Evaluate(State, RateLimitRule, nowMs)` no tiene efectos secundarios, no
toca el reloj del sistema, no conoce Redis ni memoria. Recibe el estado actual y un
timestamp y devuelve una decisión. Eso hace que los tests sean deterministas y sin
infraestructura.

---

## Arquitectura

```
HTTP Request
    │
    ▼
RateLimiterMiddleware
    │  obtiene clave del cliente (IKeyExtractor)
    │  combina con nombre de regla → bucketKey
    ▼
IBucketStore.GetAndUpdateAsync(bucketKey, rule)
    │
    ├── [Store=Memory]──► InMemoryBucketStore
    │                         Striped locking (SemaphoreSlim pool)
    │                         IMemoryCache + NeverRemove + contador
    │
    └── [Store=Redis]───► ResilientBucketStore  (decorator)
                              │
                              ├──[normal]──► RedisBucketStore
                              │                 Script Lua atómico
                              │                 Reloj interno de Redis
                              │
                              └──[Redis falla]──► InMemoryBucketStore (fallback)
                                                    Circuit breaker (Polly)
```

### Tres implementaciones de `IBucketStore`

`InMemoryBucketStore` y `RedisBucketStore` son stores reales. `ResilientBucketStore` es
un **Decorator**: no almacena nada, solo envuelve Redis con un circuit breaker de Polly y
cae al store en memoria cuando Redis no responde. El middleware nunca sabe cuál de los
tres está activo.

### `IKeyExtractor` — identificación del cliente

La clave del bucket no está hardcodeada a la IP. `IKeyExtractor.Extract(HttpContext)`
determina qué string identifica al cliente. Se incluyen dos implementaciones:

- `RemoteIpKeyExtractor` — default, usa la IP de la conexión TCP directa
- `HeaderKeyExtractor` — extrae de un header configurable (ej: `X-Api-Key`)

Cambiar de IP a API key es una línea en `Program.cs`. El middleware y el algoritmo no
se tocan.

La clave final combina cliente + regla (`"203.0.113.5:strict-rule"`) para que cada cliente
tenga buckets independientes por endpoint.

---

## Decisiones de diseño y trade-offs

### Atomicidad en memoria: Striped Locking

Un `SemaphoreSlim` por cliente crea un lock por cada IP que llega — con IPs rotativas
se convierte en un memory leak. La solución es un pool fijo de N semáforos (configurable,
default 64). Cada clave se hashea a un slot con la expresión:

```csharp
(key.GetHashCode() & int.MaxValue) % _locks.Length
```

Se usa `& int.MaxValue` en lugar de `Math.Abs` porque `Math.Abs(int.MinValue)` lanza
`OverflowException` — `-(-2.147.483.648)` desborda el rango de `int`. La máscara de bits
elimina el bit de signo sin riesgo de overflow. Dos claves pueden compartir slot (colisión
de hash), lo que reduce levemente la concurrencia pero nunca afecta la corrección.

### Atomicidad en Redis: Script Lua

Redis ejecuta scripts Lua de forma single-threaded. El ciclo leer-calcular-escribir
completo es atómico sin necesidad de WATCH/MULTI/EXEC ni reintentos. El script usa
`redis.call('TIME')` en lugar del timestamp del servidor de aplicación para eliminar
el clock skew entre instancias.

Los floats se multiplican por 1000 antes de retornar desde Lua y se dividen en C#,
porque Redis Lua no puede retornar floats al cliente directamente.

### Fail-open vs Fail-closed

La política no es uniforme — depende de qué información tiene el sistema:

| Escenario | Política | Razón |
|---|---|---|
| Redis completamente caído | Fail-open para todos | No hay info de nadie; fallar cerrado castiga usuarios legítimos |
| Caché en memoria lleno | Fail-open para clientes conocidos | Tienen estado → se sabe cómo limitarlos |
| Caché en memoria lleno | Fail-closed para clientes nuevos | Sin historial durante un ataque activo |
| Redis caído + caché lleno | Ídem anterior | La política se aplica sobre el fallback |

`CacheItemPriority.NeverRemove` garantiza que los buckets existentes no sean desalojados
para dar paso a clientes desconocidos bajo presión de memoria.

### Configuración directa vs `IOptions<T>`

Las opciones se resuelven al arrancar con `GetSection().Get<T>()` y se registran como
singleton directo. Esto evita el `IOptions<T>` wrapper y simplifica los constructores.

Trade-off aceptado: no soporta hot-reload de configuración. Para un rate limiter con
reglas que cambian raramente, un restart controlado es aceptable y más seguro que
cambios en caliente de límites.

### `LoggerMessageAttribute` — logging sin residuos para el GC

Los métodos de log están decorados con `[LoggerMessage]`, lo que genera código en
compile time que verifica el nivel habilitado antes de cualquier allocación. Sin esto,
cada llamada a `_logger.LogWarning(...)` crea un `params object[]` y boxea value types
aunque el nivel esté desactivado en producción.

### Thundering Herd — Jitter en Retry-After

Si 1000 clientes son bloqueados simultáneamente y reciben `Retry-After: 5`, todos
reintentarán exactamente al segundo 5. El jitter agrega un offset aleatorio `[0, JitterMaxSeconds)`:

```
Retry-After = Ceiling(retryAfterSeconds + Random.NextDouble() * JitterMaxSeconds)
```

El valor siempre es ≥ el tiempo real (no miente al cliente) pero distribuye los reintentos
en una ventana. Configurable; default 1.0 seg. Se puede poner en 0 para tests deterministas.

### `TimeProvider` — testabilidad del reloj

`InMemoryBucketStore` recibe `TimeProvider` por inyección. En producción se usa
`TimeProvider.System`. En tests se usa `FakeTimeProvider` de
`Microsoft.Extensions.TimeProvider.Testing`, que permite avanzar el tiempo
programáticamente sin `Thread.Sleep`.

---

## Resiliencia

### Circuit Breaker (Polly v8)

Configurado en `appsettings.json` bajo `RateLimiter.CircuitBreaker`:

| Parámetro | Qué controla |
|---|---|
| `FailureRatio` | % de fallos para abrir el circuito (default 50%) |
| `SamplingDurationSeconds` | Ventana de evaluación (default 10s) |
| `MinimumThroughput` | Mínimo de llamadas antes de evaluar (default 5) |
| `BreakDurationSeconds` | Tiempo abierto antes de probar (default 30s) |

Cuando el circuito abre, las requests van directo al fallback sin esperar el timeout
de red de Redis. Cuando se cierra, el tráfico vuelve a Redis automáticamente.

### Imagen Docker: Alpine

Las imágenes base usan la variante `alpine` (`mcr.microsoft.com/dotnet/aspnet:8.0-alpine`).
Alpine reduce el peso de la imagen final de ~220MB a ~100MB y achica la superficie de ataque
al incluir solo los componentes mínimos del sistema operativo, lo que reduce la cantidad de
vulnerabilidades potenciales que un escáner de seguridad podría encontrar.

### Validación defensiva en startup

El sistema falla rápido ante configuraciones inválidas en lugar de producir errores
en runtime difíciles de diagnosticar:

**`RateLimitRule`** — el constructor valida en construcción:
- `Name` no puede ser vacío
- `Capacity` debe ser > 0
- `RefillRate` debe ser > 0 (un valor de 0 causa división por cero en `RetryAfterSeconds`
  y en el TTL de expiración del caché)

**`RateLimiterMiddleware`** — el constructor valida que no haya PathPrefixes duplicados
y lanza `InvalidOperationException` al arrancar. Un duplicado silencioso dejaría un
endpoint sin protección o con la regla equivocada.

### Capeado de expiración en `InMemoryBucketStore`

La expiración de cada bucket se calcula como `Capacity / RefillRate * 2`. Con un
`RefillRate` extremadamente pequeño (ej: 0.0000001), este cálculo produce una expiración
de años — la entrada nunca se liberaría de memoria.

Se capea a **24 horas** como máximo: tiempo más que suficiente para cualquier bucket
real. Con ese `RefillRate` el cliente tampoco podría usar la aplicación (recuperaría
1 token cada ~115 días), lo que hace el escenario autolimitante — pero la memoria
queda protegida de todos modos.

### Métricas con OpenTelemetry y Prometheus

`RateLimiterMetrics` expone dos instrumentos usando `System.Diagnostics.Metrics` (.NET nativo):

- `rate_limiter.requests` — `Counter<long>` con tags `{rule, result}`.
  Permite queries como `rate(rate_limiter_requests_total{result="denied"}[5m])`.
- `rate_limiter.tokens_remaining` — `Histogram<double>` de tokens disponibles al momento
  del request. Útil para detectar reglas sobredimensionadas o demasiado ajustadas.

OpenTelemetry actúa como listener y los expone en `/metrics` en formato Prometheus.
Compatible con Grafana, Datadog y cualquier scraper OpenTelemetry.

El exporter (`OpenTelemetry.Exporter.Prometheus.AspNetCore`) está en beta — la
instrumentación con `System.Diagnostics.Metrics` es estable; solo el endpoint HTTP es beta.

### Reglas duplicadas

El constructor de `RateLimiterMiddleware` valida que no haya PathPrefixes duplicados en
la configuración y lanza `InvalidOperationException` al arrancar. Preferible fallar en
startup que ignorar silenciosamente una regla.

---

## Documentación de la API (Swagger)

Se agregó Swashbuckle.AspNetCore para exponer la documentación interactiva en `/swagger`.

La pieza más relevante es `RateLimitHeadersFilter`, un `IOperationFilter` que se aplica
automáticamente a todos los endpoints y agrega:

- Los headers `X-RateLimit-Limit` y `X-RateLimit-Remaining` a las respuestas exitosas
- La respuesta `429 Too Many Requests` con su descripción y el header `X-RateLimit-Retry-After`

Esto evita tener que documentar los headers en cada endpoint manualmente y garantiza
consistencia si se agregan nuevas rutas. Los endpoints del controller usan XML doc comments
(`<summary>`, `<response>`) para que Swagger muestre descripciones legibles.

---

## Testing

| Suite | Tests | Tipo | Sin infraestructura |
|---|---|---|---|
| `TokenBucketTests` | 13 | Algoritmo puro + validación de `RateLimitRule` | ✅ |
| `InMemoryBucketStoreTests` | 8 | Concurrencia, MaxEntries, expiración | ✅ |
| `ResilientBucketStoreTests` | 5 | Circuit breaker y fallo total | ✅ |
| `MiddlewareTests` | 11 | E2E con `WebApplicationFactory` | ✅ |
| **Total** | **37** | | |

Los tests de Redis (`RedisBucketStore`) requieren Docker y se pueden excluir:
```
dotnet test --filter "Category!=Redis"
```

Los tests de concurrencia (`Concurrencia_SinRaceCondition`) lanzan 20 tasks concurrentes
contra el mismo bucket y verifican que exactamente `Capacity` sean permitidos — sin
esa garantía, el rate limiter no tiene sentido en un entorno real.

---

## Uso de IA

### Proceso de trabajo

La decisión más importante del proceso fue **diseñar antes de codear**. Antes de escribir
una línea de código se crearon dos documentos de planificación disponibles en `docs/`:

- [`docs/PLAN.md`](docs/PLAN.md) — arquitectura, contratos, fases de implementación y estrategia de tests
- [`docs/EDGE_CASES.md`](docs/EDGE_CASES.md) — análisis de edge cases, contradicciones detectadas y resueltas, decisiones de diseño no obvias

Ese orden fue una decisión propia: forzar la comprensión del diseño antes de que la IA
generara código, para poder defender cada decisión desde primeros principios.

Las herramientas usadas:
- **Claude Code** como interlocutor técnico para debate de decisiones y generación de código
- **Gemini** para investigación más amplia de detalles técnicos específicos (comportamiento
  de Polly v8, detalles del script Lua en Redis, API de TimeProvider en .NET 8)

### Qué vino del análisis propio

Varias de las decisiones técnicas más relevantes surgieron de razonamiento propio, no de
sugerencias de la IA:

- **El escenario real del bot** que motivó elegir este problema — experiencia directa con
  un ataque que tumbaba el servidor diariamente en horario fijo
- **La preocupación por IPs rotativas** y el memory leak de un SemaphoreSlim por clave,
  que llevó al striped locking
- **La contradicción fail-open/fail-closed** entre la política de Redis caído y el caché
  lleno — identificada y resuelta con razonamiento propio antes de que el código fuera escrito
- **El bypass por desalojo de caché** (NeverRemove + SizeLimit + contador) como protección
  contra que un atacante reinicie su bucket al ser eviccionado
- **La idea de usar PLAN.md y EDGE_CASES.md** como documentos previos al código para
  forzar la comprensión antes de la implementación
- **El Thundering Herd con Math.Ceiling** y la necesidad del jitter para romper la
  sincronización de reintentos
- **Múltiples inconsistencias detectadas en revisión**: el `SizeLimit` hardcodeado
  desincronizado con `MaxEntries`, el `launchUrl` apuntando a `weatherforecast`, el
  `AddMemoryCache` con valor fijo que ignoraba la configuración

### Qué hizo la IA

Claude Code generó código a partir de decisiones ya tomadas, propuso alternativas técnicas
(striped locking, LuaScript, LoggerMessageAttribute, keyed services de .NET 8) y señaló
edge cases adicionales. El código fue revisado, comentado en español con razonamiento propio
y ajustado en múltiples iteraciones.

Cualquier parte del código puede explicarse desde primeros principios porque el diseño
precedió a la implementación — no al revés.

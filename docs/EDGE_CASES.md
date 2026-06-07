# Rate Limiter — Edge Cases y Preguntas de Entrevista

Problemas conocidos, decisiones de diseño no obvias y respuestas para defender
en la entrevista técnica.

---

## 1. Redis se cae — ¿rompemos todo?

No tiene que romperse. La decisión es **fail-open vs fail-closed**:

- **Fail-open** (dejar pasar todo): el sistema sigue funcionando pero pierde protección
  temporalmente. Aceptable en la mayoría de los casos — mejor disponibilidad que bloquear
  usuarios legítimos.
- **Fail-closed** (rechazar todo): más seguro ante abuso, pero un flap corto de Redis
  puede tirar el propio servicio.

**Solución implementada:** `ResilientBucketStore` (Polly `ResiliencePipeline`) wrappea
`RedisBucketStore` con un circuit breaker. Si Redis falla, cae a `InMemoryBucketStore`.
Cuando Redis vuelve, el circuito se cierra automáticamente.

Estados del circuito:
- **Cerrado:** requests van a Redis normalmente.
- **Abierto:** después de 50% de fallos en 10s (mínimo 5 calls), las siguientes requests
  van directo al fallback sin esperar timeout de red. Evita que cada request bloquee
  durante segundos mientras Redis está caído.
- **Semi-abierto:** después de 30s, una request de prueba decide si cerrar o reabrir.

**Limitación conocida del fallback:** en modo memoria, cada instancia tiene su propio
contador — el límite ya no es distribuido. Cuando Redis vuelve, los buckets en memoria
se abandonan y arrancan frescos, lo que puede permitir una pequeña ráfaga extra.
Comportamiento documentado en `DESIGN.md`.

---

## 2. Clock skew — problema silencioso en entornos distribuidos

El lazy refill calcula:

```
elapsed = (nowMs - last_refill_at) / 1000.0
```

Si dos instancias tienen relojes desfasados (común en VMs), `elapsed` puede ser negativo:
- Instancia A graba `last_refill_at = 1000`
- Instancia B recibe el siguiente request con su reloj en `950ms`
- `elapsed = (950 - 1000) / 1000 = -0.05` → tokens negativos → cliente bloqueado sin razón

**Solución inmediata:** guardar con `Math.Max(0, elapsed)`. Nunca restar tokens por
tiempo negativo.

**Solución de fondo para Redis:** usar el reloj interno de Redis dentro del script Lua
en vez de recibir el timestamp del servidor de aplicación:

```lua
local time = redis.call('TIME')
local now_ms = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
```

Todos los cálculos usan el mismo reloj. Elimina el clock skew por completo.

---

## 3. La IP real detrás de un load balancer

`context.Connection.RemoteIpAddress` devuelve la IP de quien abrió la conexión TCP.
Con un load balancer adelante, esa es la IP del load balancer — todos los usuarios
compartirían el mismo bucket.

La IP real viaja en `X-Forwarded-For`, pero ese header **puede ser falsificado**
por cualquier cliente:

```
Cliente manda:       X-Forwarded-For: 1.1.1.1
Load balancer agrega: X-Forwarded-For: 1.1.1.1, 203.0.113.5
```

Leer ingenuamente el header permite que un atacante elija qué IP se le asigna.

**Solución:** `ForwardedHeadersMiddleware` de ASP.NET Core con `KnownProxies` configurados.
Si se registra correctamente, `RemoteIpAddress` queda con la IP real del cliente y
`RemoteIpKeyExtractor` funciona sin cambios.

> En producción siempre se necesita `ForwardedHeadersMiddleware` con proxies de confianza
> configurados explícitamente.

---

## 4. Floating point en los bordes

La comparación `if (refilled < 1)` puede fallar silenciosamente por aritmética de
punto flotante. Con operaciones acumuladas, `1.0` puede resultar en `0.9999999999999998`
y el request se rechaza cuando matemáticamente debería pasar.

**La misma corrección debe aplicarse en ambas implementaciones.** De lo contrario,
un request con `refilled = 0.9999999999999998` sería aceptado por `InMemoryBucketStore`
y rechazado por `RedisBucketStore` — el mismo algoritmo tomando decisiones distintas
según el store activo.

**En C# (`TokenBucket.Evaluate`):**
```csharp
private const double TokenThreshold = 1.0 - 1e-9;

if (refilled < TokenThreshold)  // en vez de < 1
```

**En Lua (`RedisBucketStore`):**
```lua
local threshold = 1.0 - 1e-9

if refilled >= threshold then
    -- aceptar
else
    -- rechazar
end
```

---

## 5. ¿Qué requests cuentan contra el límite?

Decisión de política que hay que tomar explícitamente:

- **Todos los requests:** más simple, más seguro ante fuerza bruta y bots.
- **Solo los exitosos (2xx):** evita penalizar usuarios que reciben errores 500
  por culpa del servidor.

**Decisión para este challenge:** contar todos los requests. Si el bot pega y el
servidor devuelve 500, eso igual debe contar contra su límite.

---

## 6. Estado inicial de un cliente nuevo

Cuando llega alguien por primera vez, el bucket arranca con **capacidad completa**.
No ha usado nada, puede hacer un burst legítimo.

**Vector de ataque conocido:** un atacante que genera miles de IPs nuevas llega siempre
con bucket lleno. Combatirlo requiere capas adicionales fuera del scope del rate limiter:
CAPTCHAs, reputación de IP, ASN blocking, etc.

---

## 7. Lua script: EVAL vs EVALSHA

Cada llamada con `EVAL` y el script completo obliga a Redis a parsearlo. Con alto
volumen de requests, esto suma latencia innecesaria.

**Solución:** `SCRIPT LOAD` al iniciar → Redis devuelve un SHA → se usa `EVALSHA sha args`
en cada request. Si el script no está cacheado, Redis devuelve `NOSCRIPT` y se cae
a `EVAL` como fallback.

StackExchange.Redis hace esto automáticamente cuando se usa `LuaScript.Prepare()`
en vez de pasar el string crudo. No requiere código extra.

---

## 8. Cache eviction bajo ataque masivo — el bypass por desalojo

`IMemoryCache` con sliding expiration previene memory leaks de IPs inactivas. Pero bajo
un ataque masivo y simultáneo, el cache puede llenarse **antes** de que expiren las claves.

**El peligro:** por defecto, cuando `IMemoryCache` está bajo presión de memoria, desaloja
claves según una política LRU (Least Recently Used). Si desaloja el bucket de un atacante
activo, en el siguiente request ese atacante aparece como cliente nuevo con el bucket lleno.
El rate limiter queda neutralizado por su propio mecanismo de protección de memoria.

**Mitigación en dos capas:**

Capa 1 — `CacheItemPriority.NeverRemove` en cada entrada: el estado de clientes conocidos
nunca es desalojado por presión de memoria. Los buckets existentes se preservan siempre.

```csharp
_cache.Set(key, newState, new MemoryCacheEntryOptions
{
    SlidingExpiration = expiration,
    Priority = CacheItemPriority.NeverRemove
});
```

Capa 2 — `SizeLimit` + fail-closed solo para clientes nuevos: se define un techo de
entradas. Cuando el cache está lleno, clientes sin estado previo reciben un rechazo
en vez de un bucket lleno. Los clientes conocidos no se ven afectados.

**Por qué esto no contradice el fail-open de Redis:**

Son escenarios con threat models distintos:

- **Redis cae:** el rate limiter está completamente fuera de servicio. No hay información
  de ningún cliente. Fail-open porque castigar a todos los usuarios legítimos por una
  falla de infraestructura es peor que el riesgo temporal de tráfico no controlado.

- **Cache lleno:** el rate limiter **sigue funcionando**. Clientes conocidos tienen su
  estado intacto. Solo los clientes nuevos sin estado son rechazados. No es fail-closed
  total — es fail-closed quirúrgico para actores desconocidos mientras el sistema está
  bajo presión.

La política unificada es:
> Cuando la infraestructura falla completamente → fail-open para todos (no hay información).
> Cuando la infraestructura funciona pero está saturada → clientes conocidos operan normal,
> clientes nuevos son rechazados hasta que haya capacidad.

**Escenario combinado — Redis caído + caché en memoria lleno:**

Cuando Redis cae caemos al store en memoria. Si en ese momento un ataque de IPs rotativas
llena el caché, los nuevos clientes son rechazados incluso durante la falla de infraestructura.

Esto puede parecer una contradicción con el "fail-open cuando falla Redis", pero no lo es:
la política fail-open aplica a clientes **con historial conocido** — ellos siguen operando
normalmente. Los clientes nuevos sin historial durante una falla activa de infraestructura
son rechazados porque no hay forma de distinguirlos de atacantes, y aceptarlos podría
agotar la RAM y crashear el proceso.

Este comportamiento está cubierto por el test `FalloTotal_ClientesConocidos_SiguenOperando_NuevosRechazados`.

---

## 9. Redis Cluster y el mito del "Lua es siempre atómico"

En instancias Redis standalone, los scripts Lua son atómicos porque Redis es
single-threaded en su ejecución. Esto es correcto.

En **Redis Cluster**, los scripts Lua fallan si intentan operar sobre claves que
pertenecen a distintos hash slots (nodos diferentes).

**Aclaración importante:** nuestro script actual opera sobre **una sola clave por
ejecución** (`bucketKey`), por lo que hoy no tiene este problema en Cluster. El
conflicto aparece si el diseño evoluciona a operaciones multi-clave atómicas, por ejemplo:
comprobar un límite por IP *y* un límite global en la misma transacción.

**Solución para ese escenario — Redis Hash Tags:**
Envolver la parte variable de la clave entre `{}` fuerza a Redis Cluster a colocar
todas esas claves en el mismo slot:

```
// Sin hash tag: pueden caer en slots distintos
"203.0.113.5:strict-rule"
"203.0.113.5:global-limit"

// Con hash tag: mismo slot garantizado
"{203.0.113.5}:strict-rule"
"{203.0.113.5}:global-limit"
```

El slot se calcula solo sobre el contenido entre `{` y `}`. El script Lua puede operar
sobre ambas claves de forma atómica en un mismo nodo.

---

## 10. Thundering Herd — el pico sincronizado por el propio rate limiter

Si 500 instancias de un bot son bloqueadas simultáneamente y el servidor devuelve
`Retry-After: 4` a todas, en el segundo 4 las 500 instancias pegan al mismo tiempo.
El rate limiter creó un ataque de ráfaga sincronizado.

**Mitigación desde el cliente (jitter):** en SDKs internos o clientes controlados,
agregar un desvío aleatorio al tiempo de reintento:

```csharp
var retryAfter = TimeSpan.FromSeconds(retryAfterHeader) + TimeSpan.FromMilliseconds(random.Next(0, 1000));
```

**Mitigación desde el servidor:** en vez de devolver el ceiling exacto, agregar un
offset aleatorio pequeño. No miente — es mayor que el tiempo real — pero rompe la
sincronización sin depender de que el cliente implemente jitter:

```csharp
var jitter = Random.Shared.NextDouble(); // 0.0 a 1.0 segundos extra
var retryAfterHeader = (int)Math.Ceiling(decision.RetryAfterSeconds + jitter);
```

> Para bots externos no controlados, el jitter del servidor es la única defensa real.
> Para clientes internos, documentar el jitter como requerimiento obligatorio en la lógica
> de reintentos.

---

## Resumen para la entrevista

| Problema | Respuesta corta |
|---|---|
| Redis cae | Circuit breaker (Polly) → fallback automático a memoria; fail-open con degradación conocida |
| Clock skew | `Math.Max(0, elapsed)` + reloj interno de Redis en Lua |
| IP real tras proxy | `ForwardedHeadersMiddleware` + `KnownProxies` |
| Floating point | Epsilon en la comparación del threshold |
| ¿Qué requests cuentan? | Todos, no solo los exitosos |
| Estado inicial | Bucket lleno — limitación conocida, capas externas la mitigan |
| Performance Lua | EVALSHA automático vía `LuaScript.Prepare()` |
| Cache eviction bajo ataque | `NeverRemove` + `SizeLimit` + fail-closed para clientes nuevos |
| Redis Cluster + Lua | Una key por llamada no tiene problema; multi-key requiere hash tags `{key}` |
| Thundering Herd | Jitter en el servidor (`Retry-After` + random offset) y en clientes controlados |

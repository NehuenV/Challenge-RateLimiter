# Rate Limiter

Implementación del Capítulo 4 de *System Design Interview* (Alex Xu).

Rate limiter distribuido basado en **Token Bucket con Lazy Refill**, con soporte para
store en memoria (single-instance) y Redis (distribuido), circuit breaker automático
y jitter en Retry-After.

---

## Requisitos

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)
- [Docker](https://www.docker.com/) *(solo para el modo Redis)*

---

## Cómo correr

### Opción A — Solo Docker (recomendada, sin .NET SDK)

Solo requiere tener Docker instalado:

```bash
docker-compose -f docker-compose.memory.yml up --build
```

La API queda disponible en `http://localhost:8080/swagger`.
Usa el store en memoria — no requiere Redis.

### Opción B — Docker con Redis (distribuido)

```bash
# Levantar Redis + API en contenedores
docker-compose -f docker-compose.full.yml up --build
```

La API queda en `http://localhost:8080/swagger`.

### Opción C — .NET 8 SDK local, store en memoria

```bash
cd src/RateLimiter
dotnet run
```

La API queda en `http://localhost:5000/swagger`.

### Opción D — .NET 8 SDK local con Redis

**1. Levantar Redis en Docker:**
```bash
docker-compose up -d
```

**2. Cambiar el store en `appsettings.json`:**
```json
"RateLimiter": {
  "Store": "Redis"
}
```

**3. Correr la API:**
```bash
cd src/RateLimiter
dotnet run
```

La API queda en `http://localhost:5000/swagger` conectada a Redis en `localhost:6379`.

---

## Correr los tests

### Sin .NET SDK (solo Docker)

```bash
docker-compose -f docker-compose.tests.yml up --build --exit-code-from tests
```

Buildea un contenedor con el SDK, corre los 34 tests y muestra el resultado en consola.
`--exit-code-from tests` propaga el exit code del contenedor al shell — exit 1 si algún test falla.

### Con .NET SDK instalado

```bash
dotnet test
```

---

## Documentación interactiva (Swagger UI)

Con la API corriendo, abrí `http://localhost:5000/swagger` en el browser.

Desde ahí podés ejecutar requests directamente y ver los headers `X-RateLimit-*` en las
respuestas. Los endpoints limitados muestran la respuesta `429` con su descripción y headers.

---

## Endpoints de demo

| Endpoint | Regla |
|---|---|
| `GET /api/test` | 10 tokens, recarga 1/seg |
| `GET /api/test/strict` | 3 tokens, recarga 0.5/seg |
| `GET /health` | Sin rate limiting |

### Demo en tiempo real (PowerShell)

Con la API corriendo, ejecutar el script de demostración:

```powershell
# Apunta a la API en Docker (puerto 8080)
.\demo.ps1

# O si corrés con dotnet run (puerto 5000)
.\demo.ps1 -BaseUrl http://localhost:5000
```

El script verifica que la API esté corriendo, agota el bucket del endpoint estricto,
muestra los 429 con el header `Retry-After`, y demuestra que los buckets son
independientes por endpoint.

### Probar con el archivo HTTP incluido

El proyecto incluye `src/RateLimiter/RateLimiter.http` con requests predefinidos para
Visual Studio, Rider y VS Code (extensión REST Client). Cubre todos los endpoints,
el flujo de agotamiento del bucket y la demo con API key.

### Probar con curl

```bash
# Request normal
curl -i http://localhost:5000/api/test

# Ver qué pasa al superar el límite (bash)
for i in $(seq 1 15); do
  curl -s -o /dev/null -w "Request $i: %{http_code}\n" http://localhost:5000/api/test/strict
done

# Ver qué pasa al superar el límite (PowerShell)
1..15 | ForEach-Object {
  $r = Invoke-WebRequest http://localhost:5000/api/test/strict -ErrorAction SilentlyContinue
  "Request $_`: $($r.StatusCode)"
}
```

Respuesta cuando se supera el límite (`429 Too Many Requests`):
```
HTTP/1.1 429 Too Many Requests
X-RateLimit-Limit: 3
X-RateLimit-Remaining: 0.00
X-RateLimit-Retry-After: 2
```

---

## Configuración

Toda la configuración está en `appsettings.json` bajo la sección `RateLimiter`.
Las variables de entorno sobreescriben el archivo usando `__` como separador de secciones
(ej: `RateLimiter__Store=Redis`).

```json
{
  "RateLimiter": {
    "Store": "Memory",
    "RedisConnection": "localhost:6379,connectTimeout=1000,syncTimeout=500",
    "JitterMaxSeconds": 1.0,
    "InMemory": {
      "LockPoolSize": 64,
      "MaxEntries": 100000
    },
    "CircuitBreaker": {
      "FailureRatio": 0.5,
      "SamplingDurationSeconds": 10,
      "MinimumThroughput": 5,
      "BreakDurationSeconds": 30
    },
    "Rules": [
      {
        "PathPrefix": "/api/test/strict",
        "Name": "strict-rule",
        "Capacity": 3,
        "RefillRate": 0.5
      },
      {
        "PathPrefix": "/api/test",
        "Name": "default-rule",
        "Capacity": 10,
        "RefillRate": 1.0
      }
    ]
  }
}
```

| Parámetro | Descripción |
|---|---|
| `Store` | `"Memory"` o `"Redis"` |
| `RedisConnection` | Connection string de StackExchange.Redis |
| `JitterMaxSeconds` | Offset aleatorio en Retry-After para evitar Thundering Herd |
| `InMemory.LockPoolSize` | Slots del pool de semáforos (recomendado: potencia de 2) |
| `InMemory.MaxEntries` | Máximo de clientes simultáneos en caché |
| `CircuitBreaker.FailureRatio` | Porcentaje de fallos para abrir el circuito (0.0–1.0) |
| `CircuitBreaker.SamplingDurationSeconds` | Ventana de evaluación del circuit breaker |
| `CircuitBreaker.MinimumThroughput` | Mínimo de llamadas antes de evaluar el circuit breaker |
| `CircuitBreaker.BreakDurationSeconds` | Tiempo que el circuito permanece abierto |
| `Rules[].PathPrefix` | Prefijo de ruta que activa la regla |
| `Rules[].Name` | Identificador único de la regla |
| `Rules[].Capacity` | Máximo de tokens en el bucket |
| `Rules[].RefillRate` | Tokens recargados por segundo |

---

## Headers de respuesta

Todas las respuestas (200 y 429) incluyen:

| Header | Descripción |
|---|---|
| `X-RateLimit-Limit` | Capacidad máxima del bucket |
| `X-RateLimit-Remaining` | Tokens restantes |
| `X-RateLimit-Retry-After` | *(solo en 429)* Segundos hasta poder reintentar |

---

## Extender el identificador de cliente

Por defecto se usa la IP remota. Para limitar por API key, cambiar en `Program.cs`:

```csharp
// Reemplazar:
builder.Services.AddSingleton<IKeyExtractor, RemoteIpKeyExtractor>();

// Por:
builder.Services.AddSingleton<IKeyExtractor>(_ => new HeaderKeyExtractor("X-Api-Key"));
```

El algoritmo, los stores y el middleware no cambian.

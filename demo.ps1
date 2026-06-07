# Demostración en tiempo real del Rate Limiter.
# Requiere la API corriendo. Ver README.md para opciones de arranque.
#
# Uso:
#   .\demo.ps1                          (asume http://localhost:8080)
#   .\demo.ps1 -BaseUrl http://localhost:5000

param(
    [string]$BaseUrl = "http://localhost:8080"
)

# ── Helpers de output ────────────────────────────────────────────────────────

function Write-Header { param($msg)
    Write-Host ""
    Write-Host $msg -ForegroundColor Cyan
    Write-Host ("-" * $msg.Length) -ForegroundColor DarkCyan
}

function Write-Ok   { param($msg) Write-Host $msg -ForegroundColor Green  }
function Write-Fail { param($msg) Write-Host $msg -ForegroundColor Red    }
function Write-Info { param($msg) Write-Host $msg -ForegroundColor Yellow }

# ── Wrapper de request compatible con PS 5.1 y PS 7+ ────────────────────────

function Invoke-Request {
    param([string]$Url)

    try {
        if ($PSVersionTable.PSVersion.Major -ge 7) {
            return Invoke-WebRequest -Uri $Url -SkipHttpErrorCheck -UseBasicParsing
        }
        return Invoke-WebRequest -Uri $Url -UseBasicParsing
    }
    catch {
        $ex = $_.Exception
        if ($ex -is [System.Net.WebException] -and $ex.Response) {
            $r       = $ex.Response
            $headers = @{}
            foreach ($k in $r.Headers.Keys) { $headers[$k] = $r.Headers[$k] }
            return [PSCustomObject]@{ StatusCode = [int]$r.StatusCode; Headers = $headers }
        }
        throw
    }
}

function Show-Request {
    param([string]$Label, [string]$Url, [int]$Index)

    $r         = Invoke-Request $Url
    $status    = $r.StatusCode
    $remaining = $r.Headers["X-RateLimit-Remaining"]
    $limit     = $r.Headers["X-RateLimit-Limit"]
    $retry     = $r.Headers["X-RateLimit-Retry-After"]
    $symbol    = if ($status -eq 200) { "✓" } else { "✗" }

    if ($status -eq 200) {
        Write-Ok   "  $symbol Request $Index → $status OK              | Tokens: $remaining / $limit"
    } else {
        Write-Fail "  $symbol Request $Index → $status TOO MANY REQUESTS | Reintentar en: ${retry}s"
    }
}

# ── Demo ─────────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "  Rate Limiter — Demo" -ForegroundColor White
Write-Host "  API: $BaseUrl"      -ForegroundColor DarkGray

# 1. Health check
Write-Header "1. Verificando que la API esté corriendo"

try {
    $h = Invoke-Request "$BaseUrl/health"
    if ($h.StatusCode -eq 200) {
        Write-Ok "  API disponible en $BaseUrl"
    } else {
        Write-Fail "  La API respondió con $($h.StatusCode). Verificá que esté corriendo."
        exit 1
    }
} catch {
    Write-Fail "  No se pudo conectar a $BaseUrl"
    Write-Info "  Opciones para arrancar la API:"
    Write-Info "    docker-compose -f docker-compose.memory.yml up --build"
    Write-Info "    cd src/RateLimiter && dotnet run"
    exit 1
}

# 2. Endpoint estricto — capacity 3
Write-Header "2. /api/test/strict  (3 tokens · recarga 0.5/seg)"
Write-Info "   Enviando 5 requests seguidos — el 4to y 5to deben ser rechazados:"
Write-Host ""

for ($i = 1; $i -le 5; $i++) {
    Show-Request "strict" "$BaseUrl/api/test/strict" $i
    Start-Sleep -Milliseconds 80
}

# 3. Endpoint estándar — capacity 10
Write-Header "3. /api/test  (10 tokens · recarga 1/seg)"
Write-Info "   Enviando 12 requests seguidos — los últimos 2 deben ser rechazados:"
Write-Host ""

for ($i = 1; $i -le 12; $i++) {
    Show-Request "default" "$BaseUrl/api/test" $i
    Start-Sleep -Milliseconds 80
}

# 4. Buckets independientes
Write-Header "4. Buckets independientes por endpoint"
Write-Info "   /api/test/strict ya está agotado, pero /api/test tiene sus propios tokens."
Write-Info "   Mismo cliente, distinto bucket — ambos pueden coexistir:"
Write-Host ""

$s = Invoke-Request "$BaseUrl/api/test/strict"
$d = Invoke-Request "$BaseUrl/api/test"

$labelS = if ($s.StatusCode -eq 200) { "✓ 200 OK" } else { "✗ 429 BLOCKED" }
$labelD = if ($d.StatusCode -eq 200) { "✓ 200 OK" } else { "✗ 429 BLOCKED" }

Write-Host "  /api/test/strict  → " -NoNewline
if ($s.StatusCode -eq 200) { Write-Ok $labelS } else { Write-Fail $labelS }

Write-Host "  /api/test         → " -NoNewline
if ($d.StatusCode -eq 200) { Write-Ok $labelD } else { Write-Fail $labelD }

Write-Host ""
Write-Info "  Swagger UI: $BaseUrl/swagger"
Write-Host ""

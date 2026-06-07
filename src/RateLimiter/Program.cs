using System.Reflection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.OpenApi.Models;
using RateLimiter.Config;
using RateLimiter.KeyExtraction;
using RateLimiter.Middleware;
using RateLimiter.Storage;
using RateLimiter.Swagger;
using Serilog;
using Serilog.Formatting.Json;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// serilog configurado directo en el Di 
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .WriteTo.Console(new JsonFormatter()));

var options = builder.Configuration
    .GetSection("RateLimiter")
    .Get<RateLimiterOptions>() ?? new RateLimiterOptions();

builder.Services.AddControllers();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title       = "Rate Limiter API",
        Version     = "v1",
        Description = "Prototipo de Rate Limiter — Token Bucket con Lazy Refill. " +
                      "Todos los endpoints limitados devuelven X-RateLimit-* headers. " +
                      "Al superar el límite: 429 Too Many Requests."
    });

    // Carga los comentarios XML del controller para documentar los endpoints
    var xmlPath = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
    c.IncludeXmlComments(xmlPath);

    // Agrega automáticamente los headers de rate limit y la respuesta 429 a todos los endpoints
    c.OperationFilter<RateLimitHeadersFilter>();
});
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IKeyExtractor, RemoteIpKeyExtractor>();

// configuramos el limite maximo del cache 
builder.Services.AddMemoryCache(o => o.SizeLimit = options.InMemory.MaxEntries);

if (options.Store == "Redis")
{
    //redis como singleton
    builder.Services.AddSingleton<IConnectionMultiplexer>(
        _ => ConnectionMultiplexer.Connect(options.RedisConnection));

    //evitamos un factory gracias a net 8 
    builder.Services.AddKeyedSingleton<IBucketStore, RedisBucketStore>("primary");
    builder.Services.AddKeyedSingleton<IBucketStore, InMemoryBucketStore>("fallback");

    // ResilientBucketStore configuracion del pipeline para asignar el primary y el fallback, sin esto necesitamos el factory
    //para  saber que instancia tomar
    builder.Services.AddSingleton<IBucketStore>(sp => new ResilientBucketStore(
        sp.GetRequiredKeyedService<IBucketStore>("primary"),
        sp.GetRequiredKeyedService<IBucketStore>("fallback"),
        sp.GetRequiredService<RateLimiterOptions>(),
        sp.GetRequiredService<ILogger<ResilientBucketStore>>()));
}
else
{
    builder.Services.AddSingleton<IBucketStore, InMemoryBucketStore>();
}

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Rate Limiter v1");
    c.RoutePrefix = "swagger";
});

app.UseMiddleware<RateLimiterMiddleware>();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
   .WithTags("Health")
   .WithSummary("Estado del servicio — sin rate limiting");

app.Run();

// Necesario para que WebApplicationFactory<Program> en los tests pueda
// acceder al tipo Program definido en este ensamblado.
public partial class Program { }

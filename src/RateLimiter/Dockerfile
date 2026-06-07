# Alpine reduce el tamaño de la imagen final (~100MB vs ~220MB con la imagen base estándar)
# y achica la superficie de ataque al incluir solo los componentes mínimos del SO.
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY ["src/RateLimiter/RateLimiter.csproj", "src/RateLimiter/"]
COPY ["NuGet.config", "."]
RUN dotnet restore "src/RateLimiter/RateLimiter.csproj"
COPY . .
WORKDIR "/src/src/RateLimiter"
RUN dotnet publish "RateLimiter.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "RateLimiter.dll"]

using FluentAssertions;
using RateLimiter.Algorithm;
using RateLimiter.Config;

namespace RateLimiter.Tests;

public class TokenBucketTests
{
    private static readonly RateLimitRule Rule = new("test", capacity: 5, refillRate: 1.0);

    [Fact]
    public void PrimerRequest_BucketLleno_SePermite()
    {
        //bucket nuevo con 5 token
        var state    = new TokenBucket.State(5, 0);
        var decision = TokenBucket.Evaluate(state, Rule, nowMs: 1000);

        decision.Allowed.Should().BeTrue();
        // se consumio un token, deben quedar 4
        decision.RemainingTokens.Should().BeApproximately(4, precision: 1e-9);
    }

    [Fact]
    public void BucketVacio_RequestRechazado()
    {
        //bucket sin token y sin tiempo de recarga transcurrido
        var state    = new TokenBucket.State(0, 0);
        //no paso ni un mili segundo cuando evaluamos
        var decision = TokenBucket.Evaluate(state, Rule, nowMs: 0);

        decision.Allowed.Should().BeFalse();
        decision.RetryAfterSeconds.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TokensSeRecarganCorrectamente_ConElTiempoTranscurrido()
    {
        //bucket vacio y sin tiempo transcurrido
        var state = new TokenBucket.State(Tokens: 0, LastRefillAt: 0);

        // evaluamos al paso de 3 segundos y deberia haber 3 tokens
        //se consume uno y la peticion responde bien
        var decision = TokenBucket.Evaluate(state, Rule, nowMs: 3_000);

        decision.Allowed.Should().BeTrue();
        decision.RemainingTokens.Should().BeApproximately(2, precision: 1e-9);
    }

    [Fact]
    public void TokensNoPasanLaCapacidadMaxima()
    {
        //bucket al maximo pero sin tiempo transcurrido de recarga
        var state = new TokenBucket.State(Tokens: 3, LastRefillAt: 0);

        // validamos no desbordar nuestro bucket con mas tokens de los permitidos (deberian ser 5 no 103)
        var decision = TokenBucket.Evaluate(state, Rule, nowMs: 100_000);

        decision.Allowed.Should().BeTrue();
        //como teniamos 5 tokens y usamos uno entonces quedaron 4
        decision.RemainingTokens.Should().BeApproximately(4, precision: 1e-9);
    }

    [Fact]
    public void ClockSkew_TiempoNegativo_SeManejaSinCrash()
    {
        //simulacion de desfasaje de tiempo entre los relojes, la ultima recarga quedo por delante
        //del tiempo transcurrido 
        var state    = new TokenBucket.State(Tokens: 3, LastRefillAt: 5_000);
        var decision = TokenBucket.Evaluate(state, Rule, nowMs: 4_000);

        // no deberia generar comportamientos inesperados, el tiempo transcurrido se fija a 0 para hacer la 
        //cuenta y no dar numeros negativos
        decision.Allowed.Should().BeTrue();
        decision.RemainingTokens.Should().BeApproximately(2, precision: 1e-9);
    }

    [Fact]
    public void RetryAfterSeconds_EsExacto()
    {
        // si restan 0,3 tokens entonces deberia faltar 0.7 seg para tener un token
        var state    = new TokenBucket.State(Tokens: 0.3, LastRefillAt: 0);
        var decision = TokenBucket.Evaluate(state, Rule, nowMs: 0);

        decision.Allowed.Should().BeFalse();
        decision.RetryAfterSeconds.Should().BeApproximately(0.7, precision: 1e-6);
    }

    [Fact]
    public void DriftDePuntoFlotante_CercaDe1_SePermite()
    {
        // 0.9999999999 tokens (drift de punto flotante) debe ser aceptado por el threshold
        const double casiUnToken = 1.0 - 1e-10;
        // esto es necesario porque los binarios son pesimos manejando decimales
        var state    = new TokenBucket.State(Tokens: casiUnToken, LastRefillAt: 0);
        var decision = TokenBucket.Evaluate(state, Rule, nowMs: 0);

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public void NewState_Permitido_ReflejaElTokenConsumido()
    {
        //cargo 3 token
        var state    = new TokenBucket.State(Tokens: 3, LastRefillAt: 0);
        //evaluo al paso de un segundo (deberia tener 4 tokens para este punto)
        var decision = TokenBucket.Evaluate(state, Rule, nowMs: 1_000);
        //gaste un token
        decision.NewState.Tokens.Should().BeApproximately(3, precision: 1e-9);
        decision.NewState.LastRefillAt.Should().Be(1_000);
    }

    [Fact]
    public void NewState_Rechazado_GuardaTimestampActualizado()
    {
        // si no tengo tokens y intento una peticion desde que habia 0 
        //el tiempo debe actualizarse para hacer el calculo en la siguiente peticion
        var state    = new TokenBucket.State(Tokens: 0, LastRefillAt: 0);
        var decision = TokenBucket.Evaluate(state, Rule, nowMs: 500);

        decision.Allowed.Should().BeFalse();
        decision.NewState.LastRefillAt.Should().Be(500);
    }

    [Fact]
    public void RequestsConsecutivos_AgotanElBucket()
    {
        // 4 peticiones con 3 tokens, uno debe fallar
        var state = new TokenBucket.State(Tokens: 3, LastRefillAt: 0);

        var d1 = TokenBucket.Evaluate(state,      Rule, nowMs: 0);
        var d2 = TokenBucket.Evaluate(d1.NewState, Rule, nowMs: 0);
        var d3 = TokenBucket.Evaluate(d2.NewState, Rule, nowMs: 0);
        var d4 = TokenBucket.Evaluate(d3.NewState, Rule, nowMs: 0);

        d1.Allowed.Should().BeTrue();
        d2.Allowed.Should().BeTrue();
        d3.Allowed.Should().BeTrue();
        d4.Allowed.Should().BeFalse();
    }

    // ── Validación de RateLimitRule ──────────────────────────────────────────

    [Fact]
    public void Regla_ConRefillRateCero_LanzaExcepcion()
    {
        // RefillRate = 0 causa división por cero en RetryAfterSeconds y en el TTL del caché
        var act = () => new RateLimitRule("test", capacity: 5, refillRate: 0);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*RefillRate*");
    }

    [Fact]
    public void Regla_ConCapacidadCero_LanzaExcepcion()
    {
        var act = () => new RateLimitRule("test", capacity: 0, refillRate: 1);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*Capacity*");
    }

    [Fact]
    public void Regla_ConNombreVacio_LanzaExcepcion()
    {
        var act = () => new RateLimitRule("", capacity: 5, refillRate: 1);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*nombre*");
    }
}

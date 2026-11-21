using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using TutorFlow.Api.Llm;

namespace TutorFlow.Tests;

/// <summary>
/// Diagnóstico de fallas del proveedor: la excepción conserva el código HTTP y error.code/error.type saneado
/// (solo [A-Za-z0-9_.-], máx. 64), y nunca el mensaje libre. Sin red.
/// </summary>
public class CodigoErrorProveedorTests
{
    class Manejador(HttpStatusCode estado, string cuerpo) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(estado) { Content = new StringContent(cuerpo, Encoding.UTF8, "application/json") });
    }

    class Fabrica(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h);
    }

    static async Task<LlmException> Fallar(HttpStatusCode estado, string cuerpo)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LLM_API_KEY"] = "llave-secreta-de-prueba",
            ["LLM_BASE_URL"] = "http://proveedor.invalido/v1",
        }).Build();
        var cliente = new OpenAiCompatClient(new Fabrica(new Manejador(estado, cuerpo)), cfg);
        return await Assert.ThrowsAsync<LlmException>(() =>
            cliente.CompletarAsync("sistema", [new LlmMensaje("user", "hola")], [], CancellationToken.None));
    }

    [Fact]
    public async Task Error400_ConservaEstadoYCodigo()
    {
        var e = await Fallar(HttpStatusCode.BadRequest,
            """{"error":{"message":"texto libre con detalles","type":"invalid_request_error","code":"tool_use_failed"}}""");
        Assert.Equal(400, e.EstadoHttp);
        Assert.Equal("tool_use_failed", e.CodigoProveedor);
        Assert.Equal(LlmCausa.Http400, e.Causa);
        Assert.DoesNotContain("texto libre", e.Message);
        Assert.DoesNotContain("llave-secreta", e.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.InternalServerError, 500)]
    public async Task Error401Y500_QuedanComoOtro(HttpStatusCode estado, int esperado)
    {
        var e = await Fallar(estado, """{"error":{"code":"x"}}""");
        Assert.Equal(esperado, e.EstadoHttp);
        Assert.Equal(LlmCausa.Otro, e.Causa);
    }

    [Fact]
    public async Task SinCode_UsaType()
    {
        var e = await Fallar(HttpStatusCode.TooManyRequests, """{"error":{"message":"x","type":"tokens"}}""");
        Assert.Equal(429, e.EstadoHttp);
        Assert.Equal("tokens", e.CodigoProveedor);
        Assert.Equal(LlmCausa.Http429, e.Causa);
    }

    [Fact]
    public async Task CodigoSeSaneaYSeAcotaA64()
    {
        var largo = new string('a', 100);
        var e = await Fallar(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"a b<c>\\n{d}" + largo + "\"}}");
        Assert.Equal(64, e.CodigoProveedor!.Length);
        Assert.Matches("^[A-Za-z0-9_.-]{64}$", e.CodigoProveedor);
        Assert.StartsWith("abcd", e.CodigoProveedor);
    }

    [Theory]
    [InlineData("no es json")]
    [InlineData("{}")]
    [InlineData("""{"error":"cadena"}""")]
    [InlineData("""{"error":{"code":123}}""")]
    [InlineData("""{"error":{"code":"   <>  "}}""")]
    public async Task SinCodigoUtil_QuedaNuloPeroConservaEstado(string cuerpo)
    {
        var e = await Fallar(HttpStatusCode.BadRequest, cuerpo);
        Assert.Equal(400, e.EstadoHttp);
        Assert.Null(e.CodigoProveedor);
    }
}

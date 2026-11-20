using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TutorFlow.Tests;

public class ErroresYSaludTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _f = Fabrica.SinLlave();
    private HttpClient Cliente => _f.CreateClient();
    private const string Url = "/api/asistente/preguntar";

    public void Dispose() => _f.Dispose();

    private static StringContent Json(string cuerpo) =>
        new(cuerpo, Encoding.UTF8, "application/json");

    private static string Cuerpo(string pregunta, string rol = "estudiante", string id = "T-E1") =>
        JsonSerializer.Serialize(new { pregunta, usuario = new { rol, id } });

    private static async Task AssertError(HttpResponseMessage r, HttpStatusCode esperado)
    {
        Assert.Equal(esperado, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("error", out var e));
        Assert.Equal(JsonValueKind.String, e.ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(e.GetString()));
    }

    // CA-01 / ESC-05.6
    [Fact]
    public async Task ESC_05_6_salud_responde_200_estado_ok_sin_llave()
    {
        var r = await Cliente.GetAsync("/api/salud");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/json", r.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{\"estado\":\"ok\"}", (await r.Content.ReadAsStringAsync()).Replace(" ", ""));
    }

    // ESC-05.3
    [Fact]
    public async Task ESC_05_3_falta_usuario_400()
    {
        var r = await Cliente.PostAsync(Url, Json("{\"pregunta\":\"hola\"}"));
        await AssertError(r, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ESC_05_3_usuario_null_400()
    {
        var r = await Cliente.PostAsync(Url, Json("{\"pregunta\":\"hola\",\"usuario\":null}"));
        await AssertError(r, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ESC_05_3_falta_pregunta_400()
    {
        var r = await Cliente.PostAsync(Url, Json("{\"usuario\":{\"rol\":\"estudiante\",\"id\":\"T-E1\"}}"));
        await AssertError(r, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ESC_05_3_pregunta_vacia_400()
    {
        var r = await Cliente.PostAsync(Url, Json(Cuerpo("")));
        await AssertError(r, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ESC_05_3_pregunta_solo_espacios_400()
    {
        var r = await Cliente.PostAsync(Url, Json(Cuerpo("   \t  ")));
        await AssertError(r, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ESC_05_3_json_invalido_400()
    {
        var r = await Cliente.PostAsync(Url, Json("{\"pregunta\": \"hola\", \"usuario\": "));
        await AssertError(r, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ESC_05_3_cuerpo_vacio_400()
    {
        var r = await Cliente.PostAsync(Url, Json(""));
        await AssertError(r, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ESC_05_3_sin_cuerpo_ni_content_type_400()
    {
        var r = await Cliente.PostAsync(Url, new ByteArrayContent(Array.Empty<byte>()));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task ESC_05_3_falta_rol_400()
    {
        var r = await Cliente.PostAsync(Url, Json("{\"pregunta\":\"hola\",\"usuario\":{\"id\":\"T-E1\"}}"));
        await AssertError(r, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ESC_05_3_falta_id_400()
    {
        var r = await Cliente.PostAsync(Url, Json("{\"pregunta\":\"hola\",\"usuario\":{\"rol\":\"estudiante\"}}"));
        await AssertError(r, HttpStatusCode.BadRequest);
    }

    // ESC-05.4
    [Fact]
    public async Task ESC_05_4_rol_administrador_400()
    {
        var r = await Cliente.PostAsync(Url, Json(Cuerpo("hola", rol: "administrador")));
        await AssertError(r, HttpStatusCode.BadRequest);
    }

    // ESC-04.7
    [Fact]
    public async Task ESC_04_7_pregunta_de_1001_caracteres_400()
    {
        var r = await Cliente.PostAsync(Url, Json(Cuerpo(new string('a', 1001))));
        await AssertError(r, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ESC_04_7_pregunta_de_1000_caracteres_no_es_400()
    {
        var r = await Cliente.PostAsync(Url, Json(Cuerpo(new string('a', 1000))));
        Assert.NotEqual(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode); // sin llave
    }

    // ESC-05.5 parte "sin llave"
    [Theory]
    [InlineData("estudiante", "T-E1")]
    [InlineData("coordinador", "T-C1")]
    public async Task ESC_05_5_usuario_valido_sin_llave_503_con_mensaje(string rol, string id)
    {
        var r = await Cliente.PostAsync(Url, Json(Cuerpo("¿Cuál es la nota mínima?", rol, id)));
        await AssertError(r, HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task ESC_05_5_salud_sigue_200_despues_de_503_en_la_misma_app()
    {
        var c = Cliente;
        var p = await c.PostAsync(Url, Json(Cuerpo("¿Cuál es la nota mínima?")));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, p.StatusCode);
        var s = await c.GetAsync("/api/salud");
        Assert.Equal(HttpStatusCode.OK, s.StatusCode);
        var j = await s.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", j.GetProperty("estado").GetString());
    }

    // Contrato: errores en JSON
    [Fact]
    public async Task ESC_05_3_errores_se_devuelven_como_application_json()
    {
        var r = await Cliente.PostAsync(Url, Json(Cuerpo("")));
        Assert.Equal("application/json", r.Content.Headers.ContentType?.MediaType);
        var r2 = await Cliente.PostAsync(Url, Json(Cuerpo("hola")));
        Assert.Equal("application/json", r2.Content.Headers.ContentType?.MediaType);
    }
}

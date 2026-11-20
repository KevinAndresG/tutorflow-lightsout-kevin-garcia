using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TutorFlow.Api.Llm;

namespace TutorFlow.Tests;

/// <summary>
/// ESC-03.8 (tramo propio): las coincidencias de terceros dentro del tramo donde esta escrito el nombre completo
/// propio se ignoran; fuera de ese tramo el tercero sigue contando.
/// Datos temporales: "Paz Lopez" (T-E7) y "Paz" (T-E8), mismo programa (Alfa); T-C1 coordina a ambos.
/// </summary>
public class PermisosTramoPropioTests : IDisposable
{
    private const string Url = "/api/asistente/preguntar";
    private const string PazLopez =
        "{ \"id\": \"T-E7\", \"nombre\": \"Paz Lopez\", \"programa\": \"Programa Alfa\", \"semestre\": 1 }";
    private const string Paz =
        "{ \"id\": \"T-E8\", \"nombre\": \"Paz\", \"programa\": \"Programa Alfa\", \"semestre\": 1 }";

    private readonly List<string> _temporales = [];
    private readonly List<WebApplicationFactory<Program>> _fabricas = [];

    public void Dispose()
    {
        foreach (var f in _fabricas) f.Dispose();
        foreach (var t in _temporales) try { Directory.Delete(t, true); } catch { }
    }

    private static StringContent Cuerpo(string pregunta, string rol, string id) =>
        new(JsonSerializer.Serialize(new { pregunta, usuario = new { rol, id } }), Encoding.UTF8, "application/json");

    private string CopiaConPaz()
    {
        var raiz = Path.Combine(Path.GetTempPath(), "tutorflow-tramo-" + Guid.NewGuid().ToString("N"));
        _temporales.Add(raiz);
        foreach (var sub in new[] { "api", "reglamento" })
        {
            Directory.CreateDirectory(Path.Combine(raiz, sub));
            foreach (var f in Directory.GetFiles(Path.Combine(Fabrica.RutaFixtures, sub)))
                File.Copy(f, Path.Combine(raiz, sub, Path.GetFileName(f)));
        }
        var ruta = Path.Combine(raiz, "api", "estudiantes.json");
        var texto = File.ReadAllText(ruta).TrimEnd();
        texto = texto.Substring(0, texto.LastIndexOf(']')).TrimEnd() + "," + PazLopez + "," + Paz + "]";
        File.WriteAllText(ruta, texto);
        return raiz;
    }

    private HttpClient Cliente(string llave, LlmContador? llm)
    {
        var raiz = CopiaConPaz();
        var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Datos:Ruta", raiz);
            b.UseSetting("LLM_API_KEY", llave);
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Datos:Ruta"] = raiz,
                ["LLM_API_KEY"] = llave,
            }));
            if (llm != null)
                b.ConfigureTestServices(sv => { sv.RemoveAll<ILlmClient>(); sv.AddSingleton<ILlmClient>(llm); });
        });
        _fabricas.Add(f);
        return f.CreateClient();
    }

    private async Task Esperar403(string pregunta)
    {
        var llm = new LlmContador();
        var r = await Cliente("llave-falsa-de-prueba", llm).PostAsync(Url, Cuerpo(pregunta, "estudiante", "T-E7"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        using (var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync()))
        {
            Assert.True(doc.RootElement.TryGetProperty("error", out var e));
            Assert.False(string.IsNullOrWhiteSpace(e.GetString()));
        }
        Assert.Equal(0, llm.Llamadas);

        var s = await Cliente("", null).PostAsync(Url, Cuerpo(pregunta, "estudiante", "T-E7"));
        Assert.Equal(HttpStatusCode.Forbidden, s.StatusCode);
    }

    private async Task EsperarNo403(string pregunta)
    {
        var sin = await Cliente("", null).PostAsync(Url, Cuerpo(pregunta, "estudiante", "T-E7"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, sin.StatusCode);
        var con = await Cliente("llave-falsa-de-prueba", new LlmContador())
            .PostAsync(Url, Cuerpo(pregunta, "estudiante", "T-E7"));
        Assert.NotEqual(HttpStatusCode.Forbidden, con.StatusCode);
    }

    [Fact]
    public Task ESC_03_8_tramo_nombre_completo_propio_con_paz_dentro_no_es_403() =>
        EsperarNo403("Soy Paz Lopez, ¿cómo voy?");

    [Fact]
    public Task ESC_03_8_tramo_solo_nombre_de_pila_paz_es_tercero_403() =>
        Esperar403("¿Cómo va Paz en notas?");

    [Fact]
    public Task ESC_03_8_tramo_nombre_propio_y_paz_fuera_del_tramo_403() =>
        Esperar403("Soy Paz Lopez, ¿y cómo va Paz?");

    [Fact]
    public Task ESC_03_8_tramo_variante_mayusculas_no_es_403() =>
        EsperarNo403("SOY PAZ LOPEZ, ¿cómo voy?");

    [Fact]
    public Task ESC_03_8_tramo_variante_con_guion_no_es_403() =>
        EsperarNo403("soy paz-lopez, ¿cómo voy?");
}

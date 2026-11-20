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
/// ESC-03.8 (nombre propio repetido), ESC-03.9 (variantes de escritura) y ESC-03.10 / RN-14 (datos inconsistentes).
/// Personaje nuevo en fixtures: T-E5 "María José Prueba Cinco" (Programa Beta).
/// </summary>
public class PermisosNombresYDatosTests : IDisposable
{
    private const string Url = "/api/asistente/preguntar";
    private readonly LlmContador _llm = new();
    private readonly WebApplicationFactory<Program> _conLlave;
    private readonly WebApplicationFactory<Program> _sinLlave;
    private readonly List<string> _temporales = [];

    public PermisosNombresYDatosTests()
    {
        _conLlave = Fabrica.Crear("llave-falsa-de-prueba", s =>
        {
            s.RemoveAll<ILlmClient>();
            s.AddSingleton<ILlmClient>(_llm);
        });
        _sinLlave = Fabrica.SinLlave();
    }

    public void Dispose()
    {
        _conLlave.Dispose(); _sinLlave.Dispose();
        foreach (var f in _fabricasExtra) f.Dispose();
        foreach (var t in _temporales) try { Directory.Delete(t, true); } catch { }
    }

    private static StringContent Cuerpo(string pregunta, string rol, string id) =>
        new(JsonSerializer.Serialize(new { pregunta, usuario = new { rol, id } }), Encoding.UTF8, "application/json");

    private async Task Esperar403(string pregunta, string rol, string id)
    {
        var c = _conLlave.CreateClient();
        var r = await c.PostAsync(Url, Cuerpo(pregunta, rol, id));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        using (var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync()))
        {
            Assert.True(doc.RootElement.TryGetProperty("error", out var e));
            Assert.False(string.IsNullOrWhiteSpace(e.GetString()));
        }
        Assert.Equal(0, _llm.Llamadas);

        var s = await _sinLlave.CreateClient().PostAsync(Url, Cuerpo(pregunta, rol, id));
        Assert.Equal(HttpStatusCode.Forbidden, s.StatusCode);
    }

    private async Task EsperarNo403(string pregunta, string rol, string id)
    {
        var sin = await _sinLlave.CreateClient().PostAsync(Url, Cuerpo(pregunta, rol, id));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, sin.StatusCode);
        var con = await _conLlave.CreateClient().PostAsync(Url, Cuerpo(pregunta, rol, id));
        Assert.NotEqual(HttpStatusCode.Forbidden, con.StatusCode);
    }

    // ---------- ESC-03.8 ----------

    [Theory]
    [InlineData("Soy Alicia Prueba Uno, ¿cómo voy en mis notas?")]
    [InlineData("soy alicia prueba uno, ¿cómo voy en mis notas?")]
    [InlineData("SOY ALICIA PRUEBA UNO, ¿CÓMO VOY EN MIS NOTAS?")]
    [InlineData("Soy Alicia Prueba Uno (T-E1), ¿cómo voy en mis notas?")]
    [InlineData("Mi id es T-E1, ¿cómo voy en mis notas?")]
    public Task ESC_03_8_nombre_completo_propio_no_es_403(string pregunta) =>
        EsperarNo403(pregunta, "estudiante", "T-E1");

    [Theory]
    [InlineData("Soy Alicia, ¿cómo voy?")]
    [InlineData("¿Cómo va Alicia en sus notas?")]
    [InlineData("soy alicia, ¿cómo voy?")]
    public Task ESC_03_8_solo_nombre_de_pila_repetido_es_ambiguo_403(string pregunta) =>
        Esperar403(pregunta, "estudiante", "T-E1");

    [Fact]
    public Task ESC_03_8_nombre_completo_propio_y_nombre_de_otro_403() =>
        Esperar403("Soy Alicia Prueba Uno, ¿cómo va Bruno?", "estudiante", "T-E1");

    [Fact]
    public Task ESC_03_8_la_otra_alicia_con_su_nombre_completo_no_es_403() =>
        EsperarNo403("Soy Alicia Segunda Cuatro, ¿cómo voy en mis notas?", "estudiante", "T-E4");

    [Fact]
    public Task ESC_03_8_la_otra_alicia_solo_con_nombre_de_pila_403() =>
        Esperar403("Soy Alicia, ¿cómo voy?", "estudiante", "T-E4");

    // ---------- ESC-03.9 ----------

    [Theory]
    [InlineData("Maria Jose")]
    [InlineData("MARÍA-JOSÉ")]
    [InlineData("maría josé")]
    [InlineData("María-José")]
    [InlineData("MARIA JOSE")]
    [InlineData("maria-jose")]
    public Task ESC_03_9_variantes_de_escritura_de_otro_estudiante_403(string nombre) =>
        Esperar403($"¿Cómo van las notas de {nombre}?", "estudiante", "T-E1");

    [Theory]
    [InlineData("Soy María José Prueba Cinco, ¿cómo voy en mis notas?")]
    [InlineData("Soy María-José Prueba Cinco, ¿cómo voy en mis notas?")]
    [InlineData("soy maria jose prueba cinco, ¿cómo voy en mis notas?")]
    [InlineData("SOY MARÍA-JOSÉ PRUEBA CINCO, ¿CÓMO VOY EN MIS NOTAS?")]
    public Task ESC_03_9_la_propia_maria_jose_con_nombre_completo_no_es_403(string pregunta) =>
        EsperarNo403(pregunta, "estudiante", "T-E5");

    // ---------- ESC-03.10 / RN-14 ----------
    // El caso rol cruzado con datos válidos ya está en PermisosTests.ESC_03_6_rol_coordinador_con_id_de_estudiante_403.

    private string CopiaDeDatos()
    {
        var raiz = Path.Combine(Path.GetTempPath(), "tutorflow-invalidos-" + Guid.NewGuid().ToString("N"));
        _temporales.Add(raiz);
        foreach (var sub in new[] { "api", "reglamento" })
        {
            Directory.CreateDirectory(Path.Combine(raiz, sub));
            foreach (var f in Directory.GetFiles(Path.Combine(Fabrica.RutaFixtures, sub)))
                File.Copy(f, Path.Combine(raiz, sub, Path.GetFileName(f)));
        }
        return raiz;
    }

    private static void ArrancarCon(string ruta)
    {
        using var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Datos:Ruta", ruta);
            b.UseSetting("LLM_API_KEY", "");
        });
        f.CreateClient();
    }

    [Fact]
    public void ESC_03_10_control_datos_validos_copiados_si_arrancan()
    {
        // Control: la copia temporal sin alterar arranca; así los fallos siguientes se deben a la alteración.
        ArrancarCon(CopiaDeDatos());
    }

    [Fact]
    public void ESC_03_10_archivo_de_estudiantes_con_registro_sin_semestre_no_arranca()
    {
        var raiz = CopiaDeDatos();
        File.WriteAllText(Path.Combine(raiz, "api", "estudiantes.json"), """
        [
          { "id": "T-E1", "nombre": "Alicia Prueba Uno", "programa": "Programa Alfa", "semestre": 2 },
          { "id": "T-E9", "nombre": "Registro Incompleto Nueve", "programa": "Programa Alfa" }
        ]
        """);
        Assert.ThrowsAny<Exception>(() => ArrancarCon(raiz));
    }

    [Fact]
    public void ESC_03_10_archivo_de_coordinadores_con_registro_con_semestre_no_arranca()
    {
        var raiz = CopiaDeDatos();
        File.WriteAllText(Path.Combine(raiz, "api", "coordinadores.json"), """
        [
          { "id": "T-C1", "nombre": "Carla Coordinadora", "programa": "Programa Alfa" },
          { "id": "T-C9", "nombre": "Mezclado Nueve", "programa": "Programa Alfa", "semestre": 1 }
        ]
        """);
        Assert.ThrowsAny<Exception>(() => ArrancarCon(raiz));
    }

    [Fact]
    public void ESC_03_10_mismo_id_como_estudiante_y_como_coordinador_no_arranca()
    {
        var raiz = CopiaDeDatos();
        File.WriteAllText(Path.Combine(raiz, "api", "coordinadores.json"), """
        [
          { "id": "T-C1", "nombre": "Carla Coordinadora", "programa": "Programa Alfa" },
          { "id": "T-E1", "nombre": "Alicia Prueba Uno", "programa": "Programa Alfa" }
        ]
        """);
        Assert.ThrowsAny<Exception>(() => ArrancarCon(raiz));
    }

    // ---------- ESC-03.8: el nombre completo de un tercero extiende el del propio usuario ----------

    private WebApplicationFactory<Program> FabricaConEstudiantesExtra(string llave, LlmContador? llm, params string[] registros)
    {
        var raiz = CopiaDeDatos();
        var ruta = Path.Combine(raiz, "api", "estudiantes.json");
        var texto = File.ReadAllText(ruta).TrimEnd();
        texto = texto.Substring(0, texto.LastIndexOf(']')).TrimEnd() + "," + string.Join(",", registros) + "]";
        File.WriteAllText(ruta, texto);
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
        _fabricasExtra.Add(f);
        return f;
    }

    private readonly List<WebApplicationFactory<Program>> _fabricasExtra = [];

    private async Task Extension403(string[] registros, string pregunta, string rol, string id)
    {
        var llm = new LlmContador();
        var con = FabricaConEstudiantesExtra("llave-falsa-de-prueba", llm, registros).CreateClient();
        var r = await con.PostAsync(Url, Cuerpo(pregunta, rol, id));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        using (var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync()))
        {
            Assert.True(doc.RootElement.TryGetProperty("error", out var e));
            Assert.False(string.IsNullOrWhiteSpace(e.GetString()));
        }
        Assert.Equal(0, llm.Llamadas);

        var sin = FabricaConEstudiantesExtra("", null, registros).CreateClient();
        var s = await sin.PostAsync(Url, Cuerpo(pregunta, rol, id));
        Assert.Equal(HttpStatusCode.Forbidden, s.StatusCode);
    }

    private const string AliciaSofia =
        "{ \"id\": \"T-E6\", \"nombre\": \"Alicia Prueba Uno Sofia\", \"programa\": \"Programa Alfa\", \"semestre\": 1 }";

    [Fact]
    public Task ESC_03_8_solapamiento_nombre_completo_de_tercero_extiende_el_propio_403() =>
        Extension403([AliciaSofia], "¿Qué notas tiene Alicia Prueba Uno Sofia?", "estudiante", "T-E1");

    [Fact]
    public Task ESC_03_8_solapamiento_nombre_de_tercero_en_minusculas_403() =>
        Extension403([AliciaSofia], "¿qué notas tiene alicia prueba uno sofia?", "estudiante", "T-E1");

    [Fact]
    public async Task ESC_03_8_solapamiento_control_nombre_propio_no_es_403()
    {
        var llm = new LlmContador();
        var pregunta = "Soy Alicia Prueba Uno, ¿cómo voy?";
        var sin = FabricaConEstudiantesExtra("", null, AliciaSofia).CreateClient();
        var rs = await sin.PostAsync(Url, Cuerpo(pregunta, "estudiante", "T-E1"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rs.StatusCode);
        var con = FabricaConEstudiantesExtra("llave-falsa-de-prueba", llm, AliciaSofia).CreateClient();
        var rc = await con.PostAsync(Url, Cuerpo(pregunta, "estudiante", "T-E1"));
        Assert.NotEqual(HttpStatusCode.Forbidden, rc.StatusCode);
    }

    [Fact]
    public Task ESC_03_8_solapamiento_una_palabra_paz_pregunta_por_paz_lopez_403() =>
        Extension403(
            [
                "{ \"id\": \"T-E7\", \"nombre\": \"Paz\", \"programa\": \"Programa Alfa\", \"semestre\": 1 }",
                "{ \"id\": \"T-E8\", \"nombre\": \"Paz Lopez\", \"programa\": \"Programa Alfa\", \"semestre\": 1 }",
            ],
            "¿cómo va Paz Lopez?", "estudiante", "T-E7");
}

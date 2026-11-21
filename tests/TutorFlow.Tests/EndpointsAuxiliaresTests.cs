using System.Net;
using System.Text.Json;

namespace TutorFlow.Tests;

/// <summary>Endpoints auxiliares de SPEC sección 4: usuarios demo, texto de sección y rutas /api desconocidas.</summary>
public class EndpointsAuxiliaresTests
{
    private const string Doc = "reglamento-prueba.md";

    private static async Task<(HttpStatusCode Codigo, string Cuerpo)> Get(EntornoAuditoria e, string ruta)
    {
        var r = await e.Cliente.GetAsync(ruta);
        return (r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    private static string RutaReg(string doc, string sec) =>
        $"/api/reglamento/{Uri.EscapeDataString(doc)}/{Uri.EscapeDataString(sec)}";

    private static void AssertSinLineasDeAuditoria(EntornoAuditoria e) => Assert.Equal("", e.TextoAuditoria().Trim());

    [Fact]
    public async Task AUX_usuarios_demo_200_con_campos_exactos_y_roles_correctos_sin_llave_ni_modelo()
    {
        var llm = new FakeLlmClient();
        using var e = new EntornoAuditoria(llm, llave: "");
        var (codigo, cuerpo) = await Get(e, "/api/usuarios/demo");
        Assert.Equal(HttpStatusCode.OK, codigo);

        var arr = JsonDocument.Parse(cuerpo).RootElement;
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        foreach (var u in arr.EnumerateArray())
        {
            var nombres = u.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "id", "nombre", "programa", "rol" }, nombres);
            foreach (var p in u.EnumerateObject()) Assert.Equal(JsonValueKind.String, p.Value.ValueKind);
        }

        var mapa = arr.EnumerateArray().ToDictionary(
            u => u.GetProperty("id").GetString()!,
            u => (Rol: u.GetProperty("rol").GetString(), Nombre: u.GetProperty("nombre").GetString(), Prog: u.GetProperty("programa").GetString()));
        Assert.Equal(7, arr.GetArrayLength());
        Assert.Equal(7, mapa.Count);
        foreach (var id in new[] { "T-E1", "T-E2", "T-E3", "T-E4", "T-E5" }) Assert.Equal("estudiante", mapa[id].Rol);
        foreach (var id in new[] { "T-C1", "T-C2" }) Assert.Equal("coordinador", mapa[id].Rol);
        Assert.Equal(("estudiante", "Alicia Prueba Uno", "Programa Alfa"), mapa["T-E1"]);
        Assert.Equal(("estudiante", "Damián Ríos Tres", "Programa Alfa"), mapa["T-E3"]);
        Assert.Equal(("coordinador", "Esteban Coordinador", "Programa Beta"), mapa["T-C2"]);

        // Nada académico: ni campos ni valores de notas/asistencia.
        foreach (var prohibido in new[] { "semestre", "nota", "corte", "definitiva", "asistencia", "inasistencias", "Materia" })
            Assert.DoesNotContain(prohibido, cuerpo, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, llm.Llamadas);
        AssertSinLineasDeAuditoria(e);
    }

    [Theory]
    [InlineData("Nota mínima", "La nota mínima aprobatoria es 3.0.")]
    [InlineData("Cancelación", "Se puede cancelar una asignatura hasta la semana 8.")]
    public async Task AUX_reglamento_seccion_real_200_con_texto_de_esa_seccion(string seccion, string fragmento)
    {
        var llm = new FakeLlmClient();
        using var e = new EntornoAuditoria(llm, llave: "");
        var (codigo, cuerpo) = await Get(e, RutaReg(Doc, seccion));
        Assert.Equal(HttpStatusCode.OK, codigo);

        var raiz = JsonDocument.Parse(cuerpo).RootElement;
        Assert.Equal(new[] { "documento", "seccion", "texto" },
            raiz.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(Doc, raiz.GetProperty("documento").GetString());
        Assert.Equal(seccion, raiz.GetProperty("seccion").GetString());
        var texto = raiz.GetProperty("texto").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(texto));
        Assert.Contains(fragmento, texto);
        // No arrastra el texto de otra sección.
        var otro = seccion == "Cancelación" ? "nota mínima aprobatoria" : "cancelar una asignatura";
        Assert.DoesNotContain(otro, texto);
        Assert.Equal(0, llm.Llamadas);
        AssertSinLineasDeAuditoria(e);
    }

    [Theory]
    [InlineData("no-existe.md", "Nota mínima")]
    [InlineData(Doc, "Sección que no existe")]
    [InlineData(Doc, "Nota minima")] // la sección es el texto exacto del encabezado
    public async Task AUX_reglamento_inexistente_404_con_error(string doc, string seccion)
    {
        using var e = new EntornoAuditoria(new FakeLlmClient(), llave: "");
        var (codigo, cuerpo) = await Get(e, RutaReg(doc, seccion));
        Assert.Equal(HttpStatusCode.NotFound, codigo);
        var raiz = JsonDocument.Parse(cuerpo).RootElement;
        Assert.True(raiz.TryGetProperty("error", out var err));
        Assert.Equal(JsonValueKind.String, err.ValueKind);
        AssertSinLineasDeAuditoria(e);
    }

    [Theory]
    [InlineData("..%2F..%2Fappsettings.json", "Nota%20m%C3%ADnima")]
    [InlineData("..%5Cx", "Nota%20m%C3%ADnima")]
    [InlineData("..%2F..%2F..%2Fdata%2Fapi%2Festudiantes.json", "x")]
    [InlineData("reglamento-prueba.md", "..%2F")]
    [InlineData("reglamento-prueba.md", "..%2F..%2Fappsettings.json")]
    [InlineData("reglamento-prueba.md", "..%5C..%5Cappsettings.json")]
    [InlineData("%2Fetc%2Fpasswd", "x")]
    [InlineData("C%3A%5CWindows%5Cwin.ini", "x")]
    public async Task AUX_reglamento_traversal_404_sin_contenido_de_archivo(string doc, string seccion)
    {
        var llm = new FakeLlmClient();
        using var e = new EntornoAuditoria(llm, llave: "");
        var (codigo, cuerpo) = await Get(e, $"/api/reglamento/{doc}/{seccion}");
        Assert.Equal(HttpStatusCode.NotFound, codigo);
        foreach (var fuga in new[] { "Logging", "AllowedHosts", "Alicia Prueba", "[extensions]", "root:" })
            Assert.DoesNotContain(fuga, cuerpo);
        Assert.Equal(0, llm.Llamadas);
        AssertSinLineasDeAuditoria(e);
    }

    [Theory]
    [InlineData("/api/noexiste")]
    [InlineData("/api/asistente/otra")]
    [InlineData("/api/usuarios/demo/extra")]
    public async Task AUX_ruta_api_desconocida_404_no_es_index_html(string ruta)
    {
        using var e = new EntornoAuditoria(new FakeLlmClient(), llave: "");
        var r = await e.Cliente.GetAsync(ruta);
        var cuerpo = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.NotEqual("text/html", r.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("<html", cuerpo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AUX_arranque_sin_wwwroot_no_falla_y_salud_sigue_en_200()
    {
        using var e = new EntornoAuditoria(new FakeLlmClient(), llave: "");
        var (codigo, cuerpo) = await Get(e, "/api/salud");
        Assert.Equal(HttpStatusCode.OK, codigo);
        Assert.Equal("ok", JsonDocument.Parse(cuerpo).RootElement.GetProperty("estado").GetString());
        AssertSinLineasDeAuditoria(e);
    }
}

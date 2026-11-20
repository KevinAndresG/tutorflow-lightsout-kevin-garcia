using System.Diagnostics;
using System.Net;
using System.Text.Json;
using TutorFlow.Api.Llm;
using static TutorFlow.Tests.AuditoriaAsserts;

namespace TutorFlow.Tests;

/// <summary>
/// T5: robustez de la auditoría y del asistente (fallo de escritura, plazo total, cancelación del cliente) y
/// rendimiento de la extracción del JSON del modelo. RN-10, SPEC 4 (503) y ESC-06.1.
/// </summary>
public class AuditoriaRobustezTests
{
    private const string Cita = "{\"documento\":\"reglamento-prueba.md\",\"seccion\":\"Nota mínima\"}";

    // ---------- Fallo al escribir la auditoría ----------

    [Fact]
    public async Task ESC_06_1_si_la_auditoria_no_se_puede_escribir_la_respuesta_200_es_la_misma()
    {
        // Ruta imposible: un directorio "dentro" de un archivo existente.
        var archivo = Path.Combine(Path.GetTempPath(), "tf-no-dir-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(archivo, "x");
        try
        {
            var imposible = Path.Combine(archivo, "sub", "audit.jsonl");
            using var roto = new EntornoAuditoria(new FakeLlmClient().Final("Es 3.0.", "[" + Cita + "]", 5, 5), rutaAuditoria: imposible);
            using var sano = new EntornoAuditoria(new FakeLlmClient().Final("Es 3.0.", "[" + Cita + "]", 5, 5));

            var (c1, b1) = await roto.Preguntar("¿Cuál es la nota mínima?", "estudiante", "T-E1");
            var (c2, b2) = await sano.Preguntar("¿Cuál es la nota mínima?", "estudiante", "T-E1");

            Assert.Equal(HttpStatusCode.OK, c1);
            Assert.Equal(c2, c1);
            Assert.Equal(b2, b1);
            Assert.False(Directory.Exists(Path.Combine(archivo, "sub")));
        }
        finally { File.Delete(archivo); }
    }

    [Fact]
    public async Task ESC_06_1_si_la_auditoria_no_se_puede_escribir_el_400_y_el_403_siguen_igual()
    {
        var archivo = Path.Combine(Path.GetTempPath(), "tf-no-dir-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(archivo, "x");
        try
        {
            using var roto = new EntornoAuditoria(new FakeLlmClient(), rutaAuditoria: Path.Combine(archivo, "sub", "a.jsonl"));
            var (c1, _) = await roto.Preguntar("", "estudiante", "T-E1");
            var (c2, _) = await roto.Preguntar("notas de T-E2", "estudiante", "T-E1");
            var (c3, _) = await roto.Raw("{no json");
            Assert.Equal(HttpStatusCode.BadRequest, c1);
            Assert.Equal(HttpStatusCode.Forbidden, c2);
            Assert.Equal(HttpStatusCode.BadRequest, c3);
            var salud = await roto.Cliente.GetAsync("/api/salud");
            Assert.Equal(HttpStatusCode.OK, salud.StatusCode);
        }
        finally { File.Delete(archivo); }
    }

    // ---------- Plazo total y cancelación ----------

    [Fact]
    public async Task ESC_06_1_proveedor_que_nunca_responde_503_en_pocos_segundos_por_plazo_total_y_queda_auditado()
    {
        var colgado = new LlmFuncion(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("inalcanzable");
        });
        using var e = new EntornoAuditoria(colgado, new Dictionary<string, string?> { ["Asistente:PlazoSegundos"] = "1" });

        var sw = Stopwatch.StartNew();
        var (c, cuerpo) = await e.Preguntar("¿Cuál es la nota mínima?", "estudiante", "T-E1");
        sw.Stop();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, c);
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.5, 15);
        using var doc = JsonDocument.Parse(cuerpo);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
        Assert.Equal(1, colgado.Llamadas);

        var l = await e.UnaLinea();
        AssertForma(l);
        Assert.Equal(503, Codigo(l));
        Assert.Equal(0, Tokens(l));
        Assert.Equal(0, Costo(l));
    }

    [Fact]
    public async Task ESC_06_1_plazo_total_agotado_tras_una_vuelta_conserva_los_tokens_gastados()
    {
        var vuelta = 0;
        var llm = new LlmFuncion(async (_, ct) =>
        {
            if (Interlocked.Increment(ref vuelta) == 1)
                return new LlmRespuesta(null, [new("c1", "consultar_notas", "{\"estudianteId\":\"T-E1\"}")], new LlmUso(60, 6));
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("inalcanzable");
        });
        using var e = new EntornoAuditoria(llm, new Dictionary<string, string?> { ["Asistente:PlazoSegundos"] = "1" });
        var (c, _) = await e.Preguntar("¿Mis notas?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, c);
        var l = await e.UnaLinea();
        Assert.Equal(503, Codigo(l));
        Assert.Equal(66, Tokens(l));
        Assert.Equal(["T-E1"], Estudiantes(l));
    }

    [Fact]
    public async Task ESC_06_1_cancelacion_real_del_cliente_no_se_registra_como_fallo_del_proveedor()
    {
        var iniciada = new TaskCompletionSource();
        var cancelada = new TaskCompletionSource();
        var colgado = new LlmFuncion(async (_, ct) =>
        {
            iniciada.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelada.TrySetResult(); throw; }
            throw new InvalidOperationException("inalcanzable");
        });
        using var e = new EntornoAuditoria(colgado);   // plazo por defecto: 90 s, no interviene
        using var cts = new CancellationTokenSource();

        var tarea = e.Preguntar("¿Cuál es la nota mínima?", "estudiante", "T-E1", cts.Token);
        await iniciada.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tarea);

        // Si la cancelación llega al modelo, el servidor la observó como cancelación y no como caída del proveedor.
        await Task.WhenAny(cancelada.Task, Task.Delay(3000));
        await Task.Delay(500);
        var lineas = await e.LineasCrudas(0, 0);
        foreach (var t in lineas)
            Assert.NotEqual(503, Codigo(JsonDocument.Parse(t).RootElement));
        Assert.True(cancelada.Task.IsCompleted, "la cancelación del cliente debería propagarse al modelo");
    }

    // ---------- Rendimiento de la extracción del JSON del modelo ----------

    private static FakeLlmClient LlmConTextoGrande(string textoGrande) =>
        new FakeLlmClient().Final("calentamiento", "[]", 1, 1).Texto(textoGrande, 5, 5);

    [Fact]
    public async Task T5_rendimiento_respuesta_de_60000_llaves_sin_cerrar_termina_rapido_con_200_y_citas_vacias()
    {
        var llm = LlmConTextoGrande(new string('{', 60_000));
        using var e = new EntornoAuditoria(llm);
        var (c0, _) = await e.Preguntar("calienta", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c0);

        var sw = Stopwatch.StartNew();
        var (c, cuerpo) = await e.Preguntar("¿Cuál es la nota mínima?", "estudiante", "T-E1");
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, c);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), "tardó " + sw.Elapsed);
        using var doc = JsonDocument.Parse(cuerpo);
        Assert.Equal(0, doc.RootElement.GetProperty("citas").GetArrayLength());
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("respuesta").ValueKind);
    }

    [Fact]
    public async Task T5_rendimiento_respuesta_de_mas_de_64KB_es_texto_crudo_con_citas_vacias_aunque_contenga_un_json_valido()
    {
        var json = "{\"respuesta\":\"Es 3.0.\",\"citas\":[" + Cita + "]}";
        var llm = LlmConTextoGrande(json + new string(' ', 70_000) + " fin");
        using var e = new EntornoAuditoria(llm);
        await e.Preguntar("calienta", "estudiante", "T-E1");

        var sw = Stopwatch.StartNew();
        var (c, cuerpo) = await e.Preguntar("¿Cuál es la nota mínima?", "estudiante", "T-E1");
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, c);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), "tardó " + sw.Elapsed);
        using var doc = JsonDocument.Parse(cuerpo);
        Assert.Equal(0, doc.RootElement.GetProperty("citas").GetArrayLength());
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("respuesta").ValueKind);
    }

    [Fact]
    public async Task T5_rendimiento_texto_grande_de_llaves_balanceadas_anidadas_tambien_termina_rapido()
    {
        var llm = LlmConTextoGrande(string.Concat(Enumerable.Repeat("{\"a\":", 10_000)) + "1" + new string('}', 10_000));
        using var e = new EntornoAuditoria(llm);
        await e.Preguntar("calienta", "estudiante", "T-E1");
        var sw = Stopwatch.StartNew();
        var (c, cuerpo) = await e.Preguntar("¿Cuál es la nota mínima?", "estudiante", "T-E1");
        sw.Stop();
        Assert.Equal(HttpStatusCode.OK, c);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), "tardó " + sw.Elapsed);
        Assert.Equal(0, JsonDocument.Parse(cuerpo).RootElement.GetProperty("citas").GetArrayLength());
    }
}

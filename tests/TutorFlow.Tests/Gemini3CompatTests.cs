using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TutorFlow.Tests;

/// <summary>Manejador HTTP falso que decide la respuesta con una función y registra las peticiones. Sin red.</summary>
public sealed class ProveedorFuncion(Func<PeticionRegistrada, int, HttpResponseMessage> f) : HttpMessageHandler
{
    public List<PeticionRegistrada> Peticiones { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var cab = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers) cab[h.Key] = string.Join(",", h.Value);
        var cuerpo = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        var reg = new PeticionRegistrada(request.Method.Method, request.RequestUri!.ToString(), cab, cuerpo);
        int n;
        lock (Peticiones) { Peticiones.Add(reg); n = Peticiones.Count; }
        return f(reg, n);
    }
}

/// <summary>API con adaptadores reales, un manejador HTTP arbitrario, auditoría temporal y logs en memoria.</summary>
public sealed class EntornoT6c : IDisposable
{
    public const string Url = "/api/asistente/preguntar";
    private readonly WebApplicationFactory<Program> _fabrica;
    public string RutaAuditoria { get; } = Path.Combine(Path.GetTempPath(), "tf-t6c-" + Guid.NewGuid().ToString("N") + ".jsonl");
    public LogsEnMemoria Logs { get; } = new();
    public HttpClient Cliente { get; }

    public EntornoT6c(HttpMessageHandler proveedor, string? proveedorLlm = null, string? modelo = null, string? baseUrl = null)
    {
        var cfg = new Dictionary<string, string?>
        {
            ["Datos:Ruta"] = Fabrica.RutaFixtures,
            ["LLM_API_KEY"] = "llave-t6c-no-secreta-real",
            ["Auditoria:Ruta"] = RutaAuditoria,
        };
        if (proveedorLlm != null) cfg["LLM_PROVIDER"] = proveedorLlm;
        if (modelo != null) cfg["LLM_MODEL"] = modelo;
        if (baseUrl != null) cfg["LLM_BASE_URL"] = baseUrl;
        _fabrica = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            foreach (var (k, v) in cfg) b.UseSetting(k, v);
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(cfg));
            b.ConfigureServices(sv => sv.AddLogging(l => { l.SetMinimumLevel(LogLevel.Trace); l.AddProvider(Logs); }));
            b.ConfigureTestServices(sv => sv.AddHttpClient("llm").ConfigurePrimaryHttpMessageHandler(() => proveedor));
        });
        Cliente = _fabrica.CreateClient();
        Cliente.Timeout = TimeSpan.FromSeconds(60);
    }

    public async Task<(HttpStatusCode Codigo, string Cuerpo)> Preguntar(string pregunta, string rol = "estudiante", string id = "T-E1")
    {
        var json = JsonSerializer.Serialize(new { pregunta, usuario = new { rol, id } });
        var r = await Cliente.PostAsync(Url, new StringContent(json, Encoding.UTF8, "application/json"));
        return (r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    public string TextoAuditoria()
    {
        if (!File.Exists(RutaAuditoria)) return "";
        using var fs = new FileStream(RutaAuditoria, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    public void Dispose()
    {
        Cliente.Dispose();
        _fabrica.Dispose();
        try { if (File.Exists(RutaAuditoria)) File.Delete(RutaAuditoria); } catch { }
    }
}

/// <summary>
/// T6c: compatibilidad con Gemini 3 vía formato OpenAI. Cada tool_call puede traer extra_content con la
/// thought_signature (opaca) y debe reenviarse sin cambios en el mensaje assistant de la vuelta siguiente.
/// Diseño asumido para extra_content malformado o no objeto (pruebas T6c_7): no hay 500; se acepta 200 (se ignora)
/// o 503, y en el caso 200 nada que no sea un objeto JSON se reenvía como extra_content.
/// </summary>
public class Gemini3CompatTests
{
    private const string Cita = "{\"documento\":\"reglamento-prueba.md\",\"seccion\":\"Nota mínima\"}";
    private const string FinalJson = "{\"respuesta\":\"La nota mínima es 3.0.\",\"citas\":[" + Cita + "]}";

    // Firma opaca con barras, mas, igual, comillas, contrabarra, saltos de linea y unicode.
    private const string FirmaEspecial = "Ci4Bjz1r/X+a==\"q\\x\\\\y\n\t/áñ\U0001F600==";

    private static JsonObject Extra(string firma) =>
        new() { ["google"] = new JsonObject { ["thought_signature"] = firma } };

    private static string Final(int entrada = 5, int salida = 2) => new JsonObject
    {
        ["choices"] = new JsonArray(new JsonObject
        {
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = FinalJson },
            ["finish_reason"] = "stop",
        }),
        ["usage"] = new JsonObject { ["prompt_tokens"] = entrada, ["completion_tokens"] = salida },
    }.ToJsonString();

    /// <summary>Respuesta con tool_calls; cada llamada es (id, nombre, args, extra_content o null).</summary>
    private static string ConLlamadas(params (string Id, string Nombre, string Args, JsonNode? Extra)[] ll) => new JsonObject
    {
        ["choices"] = new JsonArray(new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = null,
                ["tool_calls"] = new JsonArray(ll.Select(l =>
                {
                    var o = new JsonObject
                    {
                        ["id"] = l.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = l.Nombre, ["arguments"] = l.Args },
                    };
                    if (l.Extra != null) o["extra_content"] = l.Extra;
                    return (JsonNode)o;
                }).ToArray()),
            },
            ["finish_reason"] = "tool_calls",
        }),
        ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 1 },
    }.ToJsonString();

    /// <summary>Variante con el cuerpo crudo del tool_call (permite extra_content con JSON arbitrario, p. ej. no objeto).</summary>
    private static string ConLlamadaCruda(string extraCrudo) =>
        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\"c1\",\"type\":\"function\"," +
        "\"function\":{\"name\":\"consultar_notas\",\"arguments\":\"{\\\"estudianteId\\\":\\\"T-E1\\\"}\"},\"extra_content\":" + extraCrudo +
        "}]},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":1}}";

    private static ProveedorFuncion Guion(params string[] respuestas) => new((_, n) =>
        n <= respuestas.Length
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respuestas[n - 1], Encoding.UTF8, "application/json") }
            : throw new InvalidOperationException("Guion agotado"));

    private static JsonArray LlamadasAssistant(PeticionRegistrada p)
    {
        var msgs = p.Json["messages"]!.AsArray();
        var asistente = msgs.Last(m => (string?)m!["role"] == "assistant" && m["tool_calls"] != null)!;
        return asistente["tool_calls"]!.AsArray();
    }

    // ---------- (1) ----------
    [Fact]
    public async Task T6c_1_ciclo_de_dos_vueltas_reenvia_extra_content_con_la_firma_sin_cambios()
    {
        var prov = Guion(
            ConLlamadas(("call_1", "consultar_notas", "{\"estudianteId\":\"T-E1\"}", Extra(FirmaEspecial))),
            Final());
        using var e = new EntornoT6c(prov);

        var (c, cuerpo) = await e.Preguntar("¿Cómo voy en mis notas?");

        Assert.Equal(HttpStatusCode.OK, c);
        Assert.Equal(2, prov.Peticiones.Count);
        var llamada = Assert.Single(LlamadasAssistant(prov.Peticiones[1]))!;
        Assert.Equal("call_1", (string?)llamada["id"]);
        Assert.Equal("function", (string?)llamada["type"]);
        var extra = llamada["extra_content"];
        Assert.NotNull(extra);
        Assert.IsType<JsonObject>(extra);
        Assert.True(JsonNode.DeepEquals(Extra(FirmaEspecial), extra), "extra_content debe reenviarse idéntico: " + extra!.ToJsonString());
        Assert.Equal(FirmaEspecial, (string?)extra["google"]!["thought_signature"]);   // cadena exacta, sin recodificar
        // El resto del tool_call no cambia.
        Assert.Equal("consultar_notas", (string?)llamada["function"]!["name"]);
        Assert.Equal("T-E1", (string?)JsonNode.Parse((string)llamada["function"]!["arguments"]!)!["estudianteId"]);
        using var doc = JsonDocument.Parse(cuerpo);
        Assert.True(doc.RootElement.GetProperty("usoDatosEstudiante").GetBoolean());
        Assert.Equal(10 + 1 + 5 + 2, doc.RootElement.GetProperty("tokens").GetInt64());
    }

    // ---------- (2) ----------
    [Fact]
    public async Task T6c_2_dos_tool_calls_solo_el_primero_con_firma_cada_uno_reenvia_lo_suyo()
    {
        var prov = Guion(
            ConLlamadas(
                ("c1", "consultar_notas", "{\"estudianteId\":\"T-E1\"}", Extra(FirmaEspecial)),
                ("c2", "consultar_asistencia", "{\"estudianteId\":\"T-E1\"}", null)),
            Final());
        using var e = new EntornoT6c(prov);

        var (c, _) = await e.Preguntar("¿Notas y asistencia?");

        Assert.Equal(HttpStatusCode.OK, c);
        var llamadas = LlamadasAssistant(prov.Peticiones[1]);
        Assert.Equal(2, llamadas.Count);
        Assert.Equal("c1", (string?)llamadas[0]!["id"]);
        Assert.True(JsonNode.DeepEquals(Extra(FirmaEspecial), llamadas[0]!["extra_content"]));
        Assert.Equal("c2", (string?)llamadas[1]!["id"]);
        Assert.False(llamadas[1]!.AsObject().ContainsKey("extra_content"), "el tool_call sin firma no debe llevar extra_content");
    }

    [Fact]
    public async Task T6c_2b_dos_tool_calls_con_firmas_distintas_no_se_intercambian()
    {
        var prov = Guion(
            ConLlamadas(
                ("c1", "consultar_notas", "{\"estudianteId\":\"T-E1\"}", Extra("FIRMA-UNO/+=")),
                ("c2", "consultar_asistencia", "{\"estudianteId\":\"T-E1\"}", Extra("FIRMA-DOS/+="))),
            Final());
        using var e = new EntornoT6c(prov);
        var (c, _) = await e.Preguntar("¿Notas y asistencia?");
        Assert.Equal(HttpStatusCode.OK, c);
        var ll = LlamadasAssistant(prov.Peticiones[1]);
        Assert.Equal("FIRMA-UNO/+=", (string?)ll[0]!["extra_content"]!["google"]!["thought_signature"]);
        Assert.Equal("FIRMA-DOS/+=", (string?)ll[1]!["extra_content"]!["google"]!["thought_signature"]);
    }

    [Fact]
    public async Task T6c_2c_la_firma_se_conserva_en_cada_vuelta_de_un_ciclo_de_tres()
    {
        var prov = Guion(
            ConLlamadas(("c1", "consultar_notas", "{\"estudianteId\":\"T-E1\"}", Extra("SIG-V1"))),
            ConLlamadas(("c2", "consultar_asistencia", "{\"estudianteId\":\"T-E1\"}", Extra("SIG-V2"))),
            Final());
        using var e = new EntornoT6c(prov);
        var (c, _) = await e.Preguntar("¿Notas y asistencia?");
        Assert.Equal(HttpStatusCode.OK, c);
        var msgs = prov.Peticiones[2].Json["messages"]!.AsArray()
            .Where(m => (string?)m!["role"] == "assistant").ToList();
        Assert.Equal(2, msgs.Count);
        Assert.Equal("SIG-V1", (string?)msgs[0]!["tool_calls"]![0]!["extra_content"]!["google"]!["thought_signature"]);
        Assert.Equal("SIG-V2", (string?)msgs[1]!["tool_calls"]![0]!["extra_content"]!["google"]!["thought_signature"]);
    }

    // ---------- (3) ----------
    [Fact]
    public async Task T6c_3_sin_extra_content_en_la_respuesta_el_cuerpo_de_la_segunda_peticion_no_lo_contiene()
    {
        var prov = Guion(
            ConLlamadas(("call_abc", "consultar_notas", "{\"estudianteId\":\"T-E1\"}", null)),
            Final());
        using var e = new EntornoT6c(prov);

        var (c, _) = await e.Preguntar("¿Cómo voy en mis notas?");

        Assert.Equal(HttpStatusCode.OK, c);
        Assert.DoesNotContain("extra_content", prov.Peticiones[1].Cuerpo);
        Assert.DoesNotContain("thought_signature", prov.Peticiones[1].Cuerpo);
        var llamada = Assert.Single(LlamadasAssistant(prov.Peticiones[1]))!.AsObject();
        Assert.Equal(["function", "id", "type"], llamada.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("extra_content", prov.Peticiones[0].Cuerpo);
    }

    // ---------- (4) ----------
    [Fact]
    public async Task T6c_4_anthropic_ignora_el_campo_y_su_cuerpo_no_cambia()
    {
        static string ToolUse(JsonNode? extra)
        {
            var bloque = new JsonObject
            {
                ["type"] = "tool_use", ["id"] = "tu_1", ["name"] = "consultar_notas",
                ["input"] = new JsonObject { ["estudianteId"] = "T-E1" },
            };
            if (extra != null) bloque["extra_content"] = extra;
            return new JsonObject
            {
                ["type"] = "message", ["role"] = "assistant", ["stop_reason"] = "tool_use",
                ["content"] = new JsonArray(bloque),
                ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 1 },
            }.ToJsonString();
        }
        var finalA = new JsonObject
        {
            ["type"] = "message", ["role"] = "assistant", ["stop_reason"] = "end_turn",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = FinalJson }),
            ["usage"] = new JsonObject { ["input_tokens"] = 7, ["output_tokens"] = 3 },
        }.ToJsonString();

        var conExtra = Guion(ToolUse(Extra(FirmaEspecial)), finalA);
        var sinExtra = Guion(ToolUse(null), finalA);
        using var e1 = new EntornoT6c(conExtra, "anthropic");
        using var e2 = new EntornoT6c(sinExtra, "anthropic");

        var (c1, _) = await e1.Preguntar("¿Cómo voy en mis notas?");
        var (c2, _) = await e2.Preguntar("¿Cómo voy en mis notas?");

        Assert.Equal(HttpStatusCode.OK, c1);
        Assert.Equal(HttpStatusCode.OK, c2);
        Assert.Equal(sinExtra.Peticiones[1].Cuerpo, conExtra.Peticiones[1].Cuerpo);
        Assert.DoesNotContain("extra_content", conExtra.Peticiones[1].Cuerpo);
        Assert.DoesNotContain("thought_signature", conExtra.Peticiones[1].Cuerpo);
        Assert.DoesNotContain("x\\\\y", conExtra.Peticiones[1].Cuerpo);
    }

    // ---------- (5) ----------
    /// <summary>Imita a Google: 400 si algún tool_call firmado en el historial pierde su extra_content (o lo altera).</summary>
    private static ProveedorFuncion ProveedorQueExigeFirma(params bool[] firmadas)
    {
        string[] nombres = ["consultar_notas", "consultar_asistencia"];
        return new ProveedorFuncion((p, n) =>
        {
            if (n == 1)
            {
                var ll = firmadas.Select((f, i) => ($"id{i}", nombres[i % 2], "{\"estudianteId\":\"T-E1\"}",
                    f ? (JsonNode?)Extra("SIG-" + i + "/+==") : null)).ToArray();
                return Ok(ConLlamadas(ll));
            }
            var asistente = p.Json["messages"]!.AsArray().Last(m => (string?)m!["role"] == "assistant" && m["tool_calls"] != null)!;
            var calls = asistente["tool_calls"]!.AsArray();
            for (var i = 0; i < firmadas.Length; i++)
            {
                var ok = i < calls.Count && (firmadas[i]
                    ? JsonNode.DeepEquals(calls[i]!["extra_content"], Extra("SIG-" + i + "/+=="))
                    : calls[i]!["extra_content"] == null);
                if (!ok)
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    { Content = new StringContent("{\"error\":{\"message\":\"Function call is missing a thought_signature\"}}", Encoding.UTF8, "application/json") };
            }
            return Ok(Final());
        });
    }

    private static HttpResponseMessage Ok(string cuerpo) =>
        new(HttpStatusCode.OK) { Content = new StringContent(cuerpo, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task T6c_5_proveedor_que_exige_la_firma_completa_el_ciclo_con_200()
    {
        var prov = ProveedorQueExigeFirma(true);
        using var e = new EntornoT6c(prov);
        var (c, cuerpo) = await e.Preguntar("¿Cómo voy en mis notas?");
        Assert.Equal(HttpStatusCode.OK, c);
        Assert.Equal(2, prov.Peticiones.Count);
        using var doc = JsonDocument.Parse(cuerpo);
        Assert.Equal("La nota mínima es 3.0.", doc.RootElement.GetProperty("respuesta").GetString());
        Assert.True(doc.RootElement.GetProperty("usoDatosEstudiante").GetBoolean());
    }

    [Fact]
    public async Task T6c_5b_proveedor_que_exige_la_firma_con_llamadas_paralelas_solo_la_primera_firmada_200()
    {
        var prov = ProveedorQueExigeFirma(true, false);
        using var e = new EntornoT6c(prov);
        var (c, _) = await e.Preguntar("¿Notas y asistencia?");
        Assert.Equal(HttpStatusCode.OK, c);
        Assert.Equal(2, prov.Peticiones.Count);
    }

    // ---------- (6) ----------
    [Fact]
    public async Task T6c_6_modelo_por_defecto_gemini_3_5_flash_con_base_por_defecto_y_max_tokens_8192()
    {
        var prov = Guion(Final());
        using var e = new EntornoT6c(prov);   // sin LLM_PROVIDER, LLM_MODEL ni LLM_BASE_URL
        var (c, _) = await e.Preguntar("¿Nota mínima?");
        Assert.Equal(HttpStatusCode.OK, c);
        var p = Assert.Single(prov.Peticiones);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", p.Url);
        Assert.Equal("gemini-3.5-flash", (string?)p.Json["model"]);
        Assert.Equal(8192, (int?)p.Json["max_tokens"]);
    }

    [Fact]
    public async Task T6c_6b_max_tokens_8192_tambien_con_modelo_y_base_configurados_y_en_la_segunda_vuelta()
    {
        var prov = Guion(ConLlamadas(("c1", "consultar_notas", "{\"estudianteId\":\"T-E1\"}", null)), Final());
        using var e = new EntornoT6c(prov, "openai", "otro-modelo", "http://proveedor.test/v1");
        var (c, _) = await e.Preguntar("¿Mis notas?");
        Assert.Equal(HttpStatusCode.OK, c);
        Assert.All(prov.Peticiones, p =>
        {
            Assert.Equal("otro-modelo", (string?)p.Json["model"]);
            Assert.Equal(8192, (int?)p.Json["max_tokens"]);
        });
    }

    [Fact]
    public async Task T6c_6c_anthropic_conserva_su_modelo_por_defecto()
    {
        var finalA = new JsonObject
        {
            ["type"] = "message", ["role"] = "assistant", ["stop_reason"] = "end_turn",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = FinalJson }),
            ["usage"] = new JsonObject { ["input_tokens"] = 7, ["output_tokens"] = 3 },
        }.ToJsonString();
        var prov = Guion(finalA);
        using var e = new EntornoT6c(prov, "anthropic");
        var (c, _) = await e.Preguntar("¿Nota mínima?");
        Assert.Equal(HttpStatusCode.OK, c);
        var p = Assert.Single(prov.Peticiones);
        Assert.Equal("claude-haiku-4-5", (string?)p.Json["model"]);
        Assert.True((int?)p.Json["max_tokens"] > 0);
    }

    // ---------- (7) ----------
    [Theory]
    [InlineData("\"una-cadena\"")]
    [InlineData("12345")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"google\":\"no-es-objeto\"}")]
    [InlineData("{\"google\":{\"thought_signature\":12345}}")]
    [InlineData("{\"google\":{\"thought_signature\":null}}")]
    [InlineData("{\"google\":[{\"thought_signature\":\"x\"}]}")]
    public async Task T6c_7_extra_content_malformado_o_no_objeto_no_produce_500(string extraCrudo)
    {
        var prov = Guion(ConLlamadaCruda(extraCrudo), Final());
        using var e = new EntornoT6c(prov);

        var (c, cuerpo) = await e.Preguntar("¿Cómo voy en mis notas?");

        Assert.True(c is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable, "se esperaba 200 o 503, no " + (int)c + ": " + cuerpo);
        if (c == HttpStatusCode.ServiceUnavailable)
        {
            using var d = JsonDocument.Parse(cuerpo);
            Assert.False(string.IsNullOrWhiteSpace(d.RootElement.GetProperty("error").GetString()));
        }
        else if (prov.Peticiones.Count > 1)
        {
            // Si se ignoró o se normalizó, nunca se reenvía algo que no sea un objeto como extra_content.
            var llamadas = LlamadasAssistant(prov.Peticiones[1]);
            foreach (var l in llamadas)
                if (l!.AsObject().TryGetPropertyValue("extra_content", out var x))
                    Assert.IsType<JsonObject>(x);
        }
        // La API sigue viva.
        var salud = await e.Cliente.GetAsync("/api/salud");
        Assert.Equal(HttpStatusCode.OK, salud.StatusCode);
    }

    [Fact]
    public async Task T6c_7b_extra_content_con_anidamiento_profundo_no_produce_500()
    {
        var profundo = string.Concat(Enumerable.Repeat("{\"a\":", 200)) + "1" + new string('}', 200);
        var prov = Guion(ConLlamadaCruda(profundo), Final());
        using var e = new EntornoT6c(prov);
        var (c, _) = await e.Preguntar("¿Cómo voy en mis notas?");
        Assert.True(c is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable, "se esperaba 200 o 503, no " + (int)c);
    }

    // ---------- (8) ----------
    private const string Marca = "SIG-OPACA-ZZ9-Qm4xT7/+secreta==";

    private static void AssertSinFirma(EntornoT6c e, string cuerpoRespuesta)
    {
        Assert.DoesNotContain(Marca, cuerpoRespuesta);
        Assert.DoesNotContain("thought_signature", cuerpoRespuesta);
        Assert.DoesNotContain("extra_content", cuerpoRespuesta);
        var aud = e.TextoAuditoria();
        Assert.NotEqual("", aud);
        Assert.DoesNotContain(Marca, aud);
        Assert.DoesNotContain("thought_signature", aud);
        Assert.DoesNotContain(Marca, e.Logs.Todo);
        Assert.DoesNotContain("thought_signature", e.Logs.Todo);
    }

    [Fact]
    public async Task T6c_8_la_firma_no_aparece_en_la_respuesta_200_ni_en_la_auditoria_ni_en_los_logs()
    {
        var prov = Guion(ConLlamadas(("c1", "consultar_notas", "{\"estudianteId\":\"T-E1\"}", Extra(Marca))), Final());
        using var e = new EntornoT6c(prov);
        var (c, cuerpo) = await e.Preguntar("¿Cómo voy en mis notas?");
        Assert.Equal(HttpStatusCode.OK, c);
        // Margen para la escritura de auditoría.
        await Task.Delay(200);
        // Sanidad: la firma sí viajó al proveedor (el reenvío ocurre), pero no sale de ahí.
        Assert.Contains("thought_signature", prov.Peticiones[1].Cuerpo);
        AssertSinFirma(e, cuerpo);
    }

    [Fact]
    public async Task T6c_8b_la_firma_no_aparece_si_la_segunda_vuelta_falla_con_503()
    {
        var prov = new ProveedorFuncion((_, n) => n == 1
            ? Ok(ConLlamadas(("c1", "consultar_notas", "{\"estudianteId\":\"T-E1\"}", Extra(Marca))))
            : new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent("{\"error\":{\"message\":\"rechazo\"}}", Encoding.UTF8, "application/json") });
        using var e = new EntornoT6c(prov);
        var (c, cuerpo) = await e.Preguntar("¿Cómo voy en mis notas?");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, c);
        await Task.Delay(200);
        AssertSinFirma(e, cuerpo);
    }

    [Fact]
    public async Task T6c_8c_la_firma_no_aparece_si_la_herramienta_deniega_403()
    {
        var prov = Guion(ConLlamadas(("c1", "consultar_notas", "{\"estudianteId\":\"T-E2\"}", Extra(Marca))));
        using var e = new EntornoT6c(prov);
        var (c, cuerpo) = await e.Preguntar("¿Cómo voy?");
        Assert.Equal(HttpStatusCode.Forbidden, c);
        await Task.Delay(200);
        AssertSinFirma(e, cuerpo);
    }
}

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TutorFlow.Tests;

/// <summary>
/// Adaptadores reales OpenAiCompatClient y AnthropicClient, de extremo a extremo por HTTP contra la API, con un
/// proveedor HTTP falso (sin red). Cubre ESC-05.5 (proveedor caído / sin llave), ESC-06.3 (llave nunca registrada),
/// ESC-06.4 (tokens de todas las vueltas), RN-12, RN-13 y SPEC 4 (variables) y 9 (proveedor).
/// </summary>
public class AdaptadoresLlmTests
{
    private const string Cita = "{\"documento\":\"reglamento-prueba.md\",\"seccion\":\"Nota mínima\"}";
    private const string CitaFalsa = "{\"documento\":\"reglamento-prueba.md\",\"seccion\":\"Inventada\"}";
    private const string FinalJson = "{\"respuesta\":\"La nota mínima es 3.0.\",\"citas\":[" + Cita + "," + CitaFalsa + "]}";

    // ---------- Constructores de respuestas del proveedor ----------

    private static string Texto(string prov, string texto, int entrada, int salida, int cacheCrea = 0, int cacheLee = 0) =>
        prov == "openai"
            ? new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = texto }, ["finish_reason"] = "stop" }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = entrada, ["completion_tokens"] = salida, ["total_tokens"] = entrada + salida },
            }.ToJsonString()
            : new JsonObject
            {
                ["id"] = "msg_1", ["type"] = "message", ["role"] = "assistant", ["stop_reason"] = "end_turn",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = texto }),
                ["usage"] = UsoAnthropic(entrada, salida, cacheCrea, cacheLee),
            }.ToJsonString();

    private static JsonObject UsoAnthropic(int entrada, int salida, int cacheCrea, int cacheLee) => new()
    {
        ["input_tokens"] = entrada, ["output_tokens"] = salida,
        ["cache_creation_input_tokens"] = cacheCrea, ["cache_read_input_tokens"] = cacheLee,
    };

    private static string Herramientas(string prov, int entrada, int salida, int cacheCrea, int cacheLee,
        params (string Id, string Nombre, string Args)[] llamadas) =>
        prov == "openai"
            ? new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant", ["content"] = null,
                        ["tool_calls"] = new JsonArray(llamadas.Select(l => (JsonNode)new JsonObject
                        {
                            ["id"] = l.Id, ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = l.Nombre, ["arguments"] = l.Args },
                        }).ToArray()),
                    },
                    ["finish_reason"] = "tool_calls",
                }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = entrada, ["completion_tokens"] = salida },
            }.ToJsonString()
            : new JsonObject
            {
                ["id"] = "msg_t", ["type"] = "message", ["role"] = "assistant", ["stop_reason"] = "tool_use",
                ["content"] = new JsonArray(llamadas.Select(l => (JsonNode)new JsonObject
                {
                    ["type"] = "tool_use", ["id"] = l.Id, ["name"] = l.Nombre, ["input"] = JsonNode.Parse(l.Args),
                }).ToArray()),
                ["usage"] = UsoAnthropic(entrada, salida, cacheCrea, cacheLee),
            }.ToJsonString();

    private static void AssertSinSecretos(EntornoProveedor e, string cuerpoRespuesta)
    {
        Assert.DoesNotContain(EntornoProveedor.Llave, cuerpoRespuesta);
        Assert.DoesNotContain(EntornoProveedor.MarcaProveedor, cuerpoRespuesta);
        Assert.DoesNotContain(EntornoProveedor.Llave, e.Logs.Todo);
    }

    private static async Task AssertSalud(EntornoProveedor e)
    {
        var r = await e.Cliente.GetAsync("/api/salud");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("{\"estado\":\"ok\"}", (await r.Content.ReadAsStringAsync()).Replace(" ", ""));
    }

    private static void Assert503(HttpStatusCode c, string cuerpo)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, c);
        using var doc = JsonDocument.Parse(cuerpo);
        Assert.True(doc.RootElement.TryGetProperty("error", out var err));
        Assert.Equal(JsonValueKind.String, err.ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(err.GetString()));
    }

    // ---------- OpenAI compatible: traducción de petición ----------

    [Fact]
    public async Task T4b_ESC_05_5_openai_por_defecto_url_cabeceras_modelo_y_cuerpo_de_la_primera_peticion()
    {
        using var e = new EntornoProveedor(null);   // sin LLM_PROVIDER: openai por defecto
        e.Proveedor.Ok(Texto("openai", FinalJson, 11, 4));

        var (c, cuerpo) = await e.Preguntar("¿Cuál es la nota mínima zorro-azul-77?");

        Assert.Equal(HttpStatusCode.OK, c);
        var p = Assert.Single(e.Proveedor.Peticiones);
        Assert.Equal("POST", p.Metodo);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", p.Url);
        Assert.Equal("Bearer " + EntornoProveedor.Llave, p.Cabecera("Authorization"));
        Assert.Contains("application/json", p.Cabecera("Content-Type"));

        var j = p.Json;
        Assert.Equal("gemini-2.5-flash", (string?)j["model"]);
        var msgs = j["messages"]!.AsArray();
        Assert.Equal(2, msgs.Count);
        Assert.Equal("system", (string?)msgs[0]!["role"]);
        var sistema = (string)msgs[0]!["content"]!;
        Assert.Contains("reglamento-prueba.md", sistema);
        Assert.Contains("La nota mínima aprobatoria es 3.0", sistema);
        Assert.DoesNotContain("zorro-azul-77", sistema);
        Assert.Equal("user", (string?)msgs[1]!["role"]);
        Assert.Contains("<pregunta>", (string)msgs[1]!["content"]!);
        Assert.Contains("zorro-azul-77", (string)msgs[1]!["content"]!);

        var tools = j["tools"]!.AsArray();
        Assert.Equal(["consultar_asistencia", "consultar_notas", "listar_estudiantes_programa"],
            tools.Select(t => (string)t!["function"]!["name"]!).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        foreach (var t in tools)
        {
            Assert.Equal("function", (string?)t!["type"]);
            Assert.False(string.IsNullOrWhiteSpace((string?)t["function"]!["description"]));
            Assert.IsType<JsonObject>(t["function"]!["parameters"]);
            Assert.Equal("object", (string?)t["function"]!["parameters"]!["type"]);
        }

        using var doc = JsonDocument.Parse(cuerpo);
        Assert.Equal(15, doc.RootElement.GetProperty("tokens").GetInt64());
        AssertSinSecretos(e, cuerpo);
    }

    [Fact]
    public async Task T4b_ESC_05_5_openai_base_y_modelo_configurados_y_proveedor_en_mayusculas()
    {
        using var e = new EntornoProveedor("OpenAI", baseUrl: "http://proveedor.test/v1", modelo: "modelo-x");
        e.Proveedor.Ok(Texto("openai", FinalJson, 1, 1));
        var (c, _) = await e.Preguntar("¿Nota mínima?");
        Assert.Equal(HttpStatusCode.OK, c);
        var p = Assert.Single(e.Proveedor.Peticiones);
        Assert.Equal("http://proveedor.test/v1/chat/completions", p.Url);
        Assert.Equal("modelo-x", (string?)p.Json["model"]);
    }

    [Fact]
    public async Task T4b_ESC_06_4_openai_ciclo_de_dos_vueltas_historial_con_tool_calls_y_resultados_tokens_sumados()
    {
        using var e = new EntornoProveedor("openai");
        e.Proveedor
            .Ok(Herramientas("openai", 100, 20, 0, 0, ("call_abc", "consultar_notas", "{\"estudianteId\":\"T-E1\"}")))
            .Ok(Texto("openai", FinalJson, 230, 45));

        var (c, cuerpo) = await e.Preguntar("¿Cómo voy en mis notas?");

        Assert.Equal(HttpStatusCode.OK, c);
        Assert.Equal(2, e.Proveedor.Cuenta);
        var msgs = e.Proveedor.Peticiones[1].Json["messages"]!.AsArray();
        Assert.Equal(4, msgs.Count);
        Assert.Equal(["system", "user", "assistant", "tool"], msgs.Select(m => (string)m!["role"]!).ToArray());

        var llamada = Assert.Single(msgs[2]!["tool_calls"]!.AsArray())!;
        Assert.Equal("call_abc", (string?)llamada["id"]);
        Assert.Equal("function", (string?)llamada["type"]);
        Assert.Equal("consultar_notas", (string?)llamada["function"]!["name"]);
        // arguments viaja como texto JSON, no como objeto.
        Assert.Equal(JsonValueKind.String, llamada["function"]!["arguments"]!.GetValueKind());
        Assert.Equal("T-E1", (string?)JsonNode.Parse((string)llamada["function"]!["arguments"]!)!["estudianteId"]);

        Assert.Equal("call_abc", (string?)msgs[3]!["tool_call_id"]);
        Assert.Contains("Materia A", (string)msgs[3]!["content"]!);
        Assert.DoesNotContain("Materia Secreta", (string)msgs[3]!["content"]!);
        // La llamada de la segunda vuelta repite las herramientas.
        Assert.Equal(3, e.Proveedor.Peticiones[1].Json["tools"]!.AsArray().Count);

        using var doc = JsonDocument.Parse(cuerpo);
        var raiz = doc.RootElement;
        Assert.Equal("La nota mínima es 3.0.", raiz.GetProperty("respuesta").GetString());
        var citas = raiz.GetProperty("citas");
        Assert.Equal(1, citas.GetArrayLength());
        Assert.Equal("reglamento-prueba.md", citas[0].GetProperty("documento").GetString());
        Assert.Equal("Nota mínima", citas[0].GetProperty("seccion").GetString());
        Assert.True(raiz.GetProperty("usoDatosEstudiante").GetBoolean());
        Assert.Equal(100 + 20 + 230 + 45, raiz.GetProperty("tokens").GetInt64());
        Assert.True(raiz.GetProperty("costoUsd").GetDouble() >= 0);
        AssertSinSecretos(e, cuerpo);
    }

    [Fact]
    public async Task T4b_ESC_06_4_openai_dos_llamadas_en_una_vuelta_producen_dos_mensajes_tool_y_tres_vueltas_suman()
    {
        using var e = new EntornoProveedor("openai");
        e.Proveedor
            .Ok(Herramientas("openai", 10, 1, 0, 0,
                ("c1", "consultar_notas", "{\"estudianteId\":\"T-E1\"}"),
                ("c2", "consultar_asistencia", "{\"estudianteId\":\"T-E1\"}")))
            .Ok(Herramientas("openai", 20, 2, 0, 0, ("c3", "consultar_notas", "{\"estudianteId\":\"T-E1\"}")))
            .Ok(Texto("openai", FinalJson, 30, 3));

        var (c, cuerpo) = await e.Preguntar("¿Notas y asistencia?");

        Assert.Equal(HttpStatusCode.OK, c);
        var tercera = e.Proveedor.Peticiones[2].Json["messages"]!.AsArray();
        Assert.Equal(["system", "user", "assistant", "tool", "tool", "assistant", "tool"],
            tercera.Select(m => (string)m!["role"]!).ToArray());
        Assert.Equal(2, tercera[2]!["tool_calls"]!.AsArray().Count);
        Assert.Equal(["c1", "c2"], new[] { (string)tercera[3]!["tool_call_id"]!, (string)tercera[4]!["tool_call_id"]! });
        using var doc = JsonDocument.Parse(cuerpo);
        Assert.Equal(11 + 22 + 33, doc.RootElement.GetProperty("tokens").GetInt64());
    }

    [Fact]
    public async Task T4b_ESC_06_4_openai_respuesta_final_es_texto_plano_no_json_va_cruda_con_citas_vacias()
    {
        using var e = new EntornoProveedor("openai");
        e.Proveedor.Ok(Texto("openai", "Hola, no sé de qué hablas", 5, 5));
        var (c, cuerpo) = await e.Preguntar("¿Hola?");
        Assert.Equal(HttpStatusCode.OK, c);
        using var doc = JsonDocument.Parse(cuerpo);
        Assert.Equal("Hola, no sé de qué hablas", doc.RootElement.GetProperty("respuesta").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("citas").GetArrayLength());
    }

    // ---------- Anthropic: traducción de petición ----------

    [Fact]
    public async Task T4b_ESC_05_5_anthropic_por_defecto_url_cabeceras_modelo_system_con_cache_y_herramientas()
    {
        using var e = new EntornoProveedor("anthropic");
        e.Proveedor.Ok(Texto("anthropic", FinalJson, 11, 4));

        var (c, cuerpo) = await e.Preguntar("¿Cuál es la nota mínima zorro-azul-77?");

        Assert.Equal(HttpStatusCode.OK, c);
        var p = Assert.Single(e.Proveedor.Peticiones);
        Assert.Equal("POST", p.Metodo);
        Assert.Equal("https://api.anthropic.com/v1/messages", p.Url);
        Assert.Equal(EntornoProveedor.Llave, p.Cabecera("x-api-key"));
        Assert.Equal("2023-06-01", p.Cabecera("anthropic-version"));
        Assert.Contains("application/json", p.Cabecera("Content-Type"));

        var j = p.Json;
        Assert.Equal("claude-haiku-4-5", (string?)j["model"]);
        Assert.True((int?)j["max_tokens"] > 0, "la API de Anthropic exige max_tokens");

        var sistema = j["system"]!.AsArray();
        var bloque = Assert.Single(sistema)!;
        Assert.Equal("text", (string?)bloque["type"]);
        Assert.Contains("reglamento-prueba.md", (string)bloque["text"]!);
        Assert.Contains("La nota mínima aprobatoria es 3.0", (string)bloque["text"]!);
        Assert.DoesNotContain("zorro-azul-77", (string)bloque["text"]!);
        Assert.Equal("ephemeral", (string?)bloque["cache_control"]!["type"]);

        var msgs = j["messages"]!.AsArray();
        var user = Assert.Single(msgs)!;
        Assert.Equal("user", (string?)user["role"]);
        var cont = Assert.Single(user["content"]!.AsArray())!;
        Assert.Equal("text", (string?)cont["type"]);
        Assert.Contains("<pregunta>", (string)cont["text"]!);
        Assert.Contains("zorro-azul-77", (string)cont["text"]!);

        var tools = j["tools"]!.AsArray();
        Assert.Equal(["consultar_asistencia", "consultar_notas", "listar_estudiantes_programa"],
            tools.Select(t => (string)t!["name"]!).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        foreach (var t in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace((string?)t!["description"]));
            Assert.IsType<JsonObject>(t["input_schema"]);
            Assert.Equal("object", (string?)t["input_schema"]!["type"]);
        }

        using var doc = JsonDocument.Parse(cuerpo);
        Assert.Equal(15, doc.RootElement.GetProperty("tokens").GetInt64());
        AssertSinSecretos(e, cuerpo);
    }

    [Fact]
    public async Task T4b_ESC_05_5_anthropic_base_y_modelo_configurados_y_proveedor_en_mayusculas()
    {
        using var e = new EntornoProveedor("ANTHROPIC", baseUrl: "http://proveedor.test", modelo: "claude-x");
        e.Proveedor.Ok(Texto("anthropic", FinalJson, 1, 1));
        var (c, _) = await e.Preguntar("¿Nota mínima?");
        Assert.Equal(HttpStatusCode.OK, c);
        var p = Assert.Single(e.Proveedor.Peticiones);
        Assert.Equal("http://proveedor.test/v1/messages", p.Url);
        Assert.Equal("claude-x", (string?)p.Json["model"]);
    }

    [Fact]
    public async Task T4b_ESC_06_4_anthropic_ciclo_de_dos_vueltas_tool_use_tool_result_y_tokens_con_cache()
    {
        using var e = new EntornoProveedor("anthropic");
        e.Proveedor
            .Ok(Herramientas("anthropic", 10, 5, 100, 1000, ("toolu_1", "consultar_notas", "{\"estudianteId\":\"T-E1\"}")))
            .Ok(Texto("anthropic", FinalJson, 7, 3, 0, 2000));

        var (c, cuerpo) = await e.Preguntar("¿Cómo voy en mis notas?");

        Assert.Equal(HttpStatusCode.OK, c);
        Assert.Equal(2, e.Proveedor.Cuenta);
        var msgs = e.Proveedor.Peticiones[1].Json["messages"]!.AsArray();
        Assert.Equal(["user", "assistant", "user"], msgs.Select(m => (string)m!["role"]!).ToArray());

        var uso = Assert.Single(msgs[1]!["content"]!.AsArray(), b => (string?)b!["type"] == "tool_use")!;
        Assert.Equal("toolu_1", (string?)uso["id"]);
        Assert.Equal("consultar_notas", (string?)uso["name"]);
        Assert.IsType<JsonObject>(uso["input"]);   // objeto, no texto
        Assert.Equal("T-E1", (string?)uso["input"]!["estudianteId"]);

        var res = Assert.Single(msgs[2]!["content"]!.AsArray())!;
        Assert.Equal("tool_result", (string?)res["type"]);
        Assert.Equal("toolu_1", (string?)res["tool_use_id"]);
        Assert.Contains("Materia A", res["content"]!.ToJsonString());
        Assert.DoesNotContain("Materia Secreta", res["content"]!.ToJsonString());

        using var doc = JsonDocument.Parse(cuerpo);
        var raiz = doc.RootElement;
        Assert.Equal("La nota mínima es 3.0.", raiz.GetProperty("respuesta").GetString());
        Assert.Equal(1, raiz.GetProperty("citas").GetArrayLength());
        Assert.True(raiz.GetProperty("usoDatosEstudiante").GetBoolean());
        // vuelta 1: 10+100+1000+5 ; vuelta 2: 7+0+2000+3
        Assert.Equal(1115 + 2010, raiz.GetProperty("tokens").GetInt64());
        Assert.True(raiz.GetProperty("costoUsd").GetDouble() >= 0);
        AssertSinSecretos(e, cuerpo);
    }

    [Fact]
    public async Task T4b_ESC_06_4_anthropic_tool_results_consecutivos_se_agrupan_en_un_solo_mensaje_user()
    {
        using var e = new EntornoProveedor("anthropic");
        e.Proveedor
            .Ok(Herramientas("anthropic", 10, 1, 0, 0,
                ("tu_1", "consultar_notas", "{\"estudianteId\":\"T-E1\"}"),
                ("tu_2", "consultar_asistencia", "{\"estudianteId\":\"T-E1\"}")))
            .Ok(Texto("anthropic", FinalJson, 20, 2));

        var (c, _) = await e.Preguntar("¿Notas y asistencia?");

        Assert.Equal(HttpStatusCode.OK, c);
        var msgs = e.Proveedor.Peticiones[1].Json["messages"]!.AsArray();
        Assert.Equal(["user", "assistant", "user"], msgs.Select(m => (string)m!["role"]!).ToArray());
        Assert.Equal(2, msgs[1]!["content"]!.AsArray().Count(b => (string?)b!["type"] == "tool_use"));
        var resultados = msgs[2]!["content"]!.AsArray();
        Assert.Equal(2, resultados.Count);
        Assert.All(resultados, r => Assert.Equal("tool_result", (string?)r!["type"]));
        Assert.Equal(["tu_1", "tu_2"], resultados.Select(r => (string)r!["tool_use_id"]!).ToArray());
    }

    [Fact]
    public async Task T4b_ESC_06_4_anthropic_texto_mas_tool_use_en_la_misma_respuesta_y_uso_sin_campos_de_cache()
    {
        using var e = new EntornoProveedor("anthropic");
        var conTexto = new JsonObject
        {
            ["type"] = "message", ["role"] = "assistant", ["stop_reason"] = "tool_use",
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = "Voy a consultar." },
                new JsonObject { ["type"] = "tool_use", ["id"] = "tu_9", ["name"] = "consultar_notas", ["input"] = new JsonObject { ["estudianteId"] = "T-E1" } }),
            ["usage"] = new JsonObject { ["input_tokens"] = 8, ["output_tokens"] = 2 },   // sin campos de cache
        }.ToJsonString();
        var final = new JsonObject
        {
            ["type"] = "message", ["role"] = "assistant", ["stop_reason"] = "end_turn",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = FinalJson }),
            ["usage"] = new JsonObject { ["input_tokens"] = 9, ["output_tokens"] = 1 },
        }.ToJsonString();
        e.Proveedor.Ok(conTexto).Ok(final);

        var (c, cuerpo) = await e.Preguntar("¿Mis notas?");

        Assert.Equal(HttpStatusCode.OK, c);
        using var doc = JsonDocument.Parse(cuerpo);
        Assert.Equal(20, doc.RootElement.GetProperty("tokens").GetInt64());
        Assert.True(doc.RootElement.GetProperty("usoDatosEstudiante").GetBoolean());
    }

    [Fact]
    public async Task T4b_ESC_06_4_anthropic_permiso_denegado_en_herramienta_403_y_no_hay_segunda_llamada()
    {
        using var e = new EntornoProveedor("anthropic");
        e.Proveedor.Ok(Herramientas("anthropic", 10, 1, 0, 0, ("tu_1", "consultar_notas", "{\"estudianteId\":\"T-E2\"}")));
        var (c, cuerpo) = await e.Preguntar("¿Cómo voy?");
        Assert.Equal(HttpStatusCode.Forbidden, c);
        Assert.Equal(1, e.Proveedor.Cuenta);
        Assert.DoesNotContain("Materia Secreta", cuerpo);
        AssertSinSecretos(e, cuerpo);
    }

    [Fact]
    public async Task T4b_ESC_06_4_openai_permiso_denegado_en_herramienta_403_y_no_hay_segunda_llamada()
    {
        using var e = new EntornoProveedor("openai");
        e.Proveedor.Ok(Herramientas("openai", 10, 1, 0, 0, ("c1", "consultar_notas", "{\"estudianteId\":\"T-E2\"}")));
        var (c, cuerpo) = await e.Preguntar("¿Cómo voy?");
        Assert.Equal(HttpStatusCode.Forbidden, c);
        Assert.Equal(1, e.Proveedor.Cuenta);
        Assert.DoesNotContain("Materia Secreta", cuerpo);
        AssertSinSecretos(e, cuerpo);
    }

    // ---------- Fallos del proveedor (ambos adaptadores) ----------

    [Theory]
    [InlineData("openai", 401)]
    [InlineData("openai", 429)]
    [InlineData("openai", 500)]
    [InlineData("openai", 503)]
    [InlineData("openai", 400)]
    [InlineData("anthropic", 401)]
    [InlineData("anthropic", 429)]
    [InlineData("anthropic", 500)]
    [InlineData("anthropic", 529)]
    [InlineData("anthropic", 400)]
    public async Task T4b_ESC_05_5_proveedor_responde_error_503_sin_filtrar_llave_ni_cuerpo_del_proveedor(string prov, int estado)
    {
        using var e = new EntornoProveedor(prov);
        e.Proveedor.Responde((HttpStatusCode)estado,
            "{\"error\":{\"message\":\"" + EntornoProveedor.MarcaProveedor + " llave " + EntornoProveedor.Llave + "\"}}");

        var (c, cuerpo) = await e.Preguntar("¿Cuál es la nota mínima?");

        Assert503(c, cuerpo);
        Assert.Equal(1, e.Proveedor.Cuenta);
        AssertSinSecretos(e, cuerpo);
        await AssertSalud(e);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task T4b_ESC_05_5_error_en_la_segunda_vuelta_503(string prov)
    {
        using var e = new EntornoProveedor(prov);
        e.Proveedor
            .Ok(Herramientas(prov, 10, 1, 0, 0, ("id1", "consultar_notas", "{\"estudianteId\":\"T-E1\"}")))
            .Responde(HttpStatusCode.InternalServerError, EntornoProveedor.MarcaProveedor);
        var (c, cuerpo) = await e.Preguntar("¿Mis notas?");
        Assert503(c, cuerpo);
        AssertSinSecretos(e, cuerpo);
    }

    public static IEnumerable<object[]> Excepciones()
    {
        foreach (var prov in new[] { "openai", "anthropic" })
        {
            yield return [prov, "timeout", new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout")];
            yield return [prov, "timeout_interno", new TimeoutException("agotado " + EntornoProveedor.Llave)];
            yield return [prov, "red", new HttpRequestException("No such host is known " + EntornoProveedor.Llave)];
            yield return [prov, "red_socket", new HttpRequestException("Connection refused", new System.Net.Sockets.SocketException(10061))];
        }
    }

    [Theory]
    [MemberData(nameof(Excepciones))]
    public async Task T4b_ESC_05_5_timeout_o_red_caida_503_sin_llave(string prov, string caso, Exception ex)
    {
        _ = caso;
        using var e = new EntornoProveedor(prov);
        e.Proveedor.Lanza(ex);
        var (c, cuerpo) = await e.Preguntar("¿Cuál es la nota mínima?");
        Assert503(c, cuerpo);
        AssertSinSecretos(e, cuerpo);
        await AssertSalud(e);
    }

    public static IEnumerable<object[]> CuerposInvalidos() =>
    [
        ["openai", "<html>Bad gateway " + EntornoProveedor.MarcaProveedor + "</html>"],
        ["openai", "{ no es json " + EntornoProveedor.MarcaProveedor],
        ["openai", ""],
        ["openai", "{}"],
        ["openai", "{\"choices\":[]}"],
        ["openai", "{\"choices\":[{}]}"],
        ["openai", "[1,2,3]"],
        ["openai", "null"],
        ["anthropic", "<html>Bad gateway " + EntornoProveedor.MarcaProveedor + "</html>"],
        ["anthropic", "{ no es json " + EntornoProveedor.MarcaProveedor],
        ["anthropic", ""],
        ["anthropic", "{}"],
        ["anthropic", "{\"content\":\"texto suelto\"}"],
        ["anthropic", "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"" + EntornoProveedor.MarcaProveedor + "\"}}"],
        ["anthropic", "[1,2,3]"],
        ["anthropic", "null"],
    ];

    [Theory]
    [MemberData(nameof(CuerposInvalidos))]
    public async Task T4b_ESC_05_5_respuesta_2xx_con_json_invalido_o_sin_campos_esperados_503(string prov, string cuerpoProveedor)
    {
        using var e = new EntornoProveedor(prov);
        e.Proveedor.Ok(cuerpoProveedor);
        var (c, cuerpo) = await e.Preguntar("¿Cuál es la nota mínima?");
        Assert503(c, cuerpo);
        AssertSinSecretos(e, cuerpo);
        await AssertSalud(e);
    }

    // ---------- Llave ----------

    [Theory]
    [InlineData("openai", "")]
    [InlineData("openai", "   ")]
    [InlineData("anthropic", "")]
    [InlineData("anthropic", "   ")]
    public async Task T4b_ESC_05_5_sin_llave_503_sin_llamar_al_proveedor_y_salud_200(string prov, string llave)
    {
        using var e = new EntornoProveedor(prov, llave);
        var (c, cuerpo) = await e.Preguntar("¿Cuál es la nota mínima?");
        Assert503(c, cuerpo);
        Assert.Equal(0, e.Proveedor.Cuenta);
        await AssertSalud(e);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task T4b_ESC_06_3_la_llave_no_aparece_en_logs_ni_respuestas_en_exito_403_400_y_503(string prov)
    {
        using var e = new EntornoProveedor(prov);
        e.Proveedor
            .Ok(Texto(prov, FinalJson, 1, 1))                                   // 200
            .Responde(HttpStatusCode.Unauthorized, "clave " + EntornoProveedor.Llave);   // 503
        var (c1, b1) = await e.Preguntar("¿Nota mínima?");
        var (c2, b2) = await e.Preguntar("¿Notas de Bruno Prueba Dos?");   // 403 antes del modelo
        var (c3, b3) = await e.Preguntar("");                               // 400
        var (c4, b4) = await e.Preguntar("¿Cancelación?");                  // 503 por 401 del proveedor
        Assert.Equal(HttpStatusCode.OK, c1);
        Assert.Equal(HttpStatusCode.Forbidden, c2);
        Assert.Equal(HttpStatusCode.BadRequest, c3);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, c4);
        Assert.Equal(2, e.Proveedor.Cuenta);
        foreach (var b in new[] { b1, b2, b3, b4 }) AssertSinSecretos(e, b);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task T4b_ESC_05_5_la_llave_se_lee_por_peticion_y_va_en_la_cabecera_correcta(string prov)
    {
        using var e = new EntornoProveedor(prov, "llave-distinta-123");
        e.Proveedor.Ok(Texto(prov, FinalJson, 1, 1));
        var (c, _) = await e.Preguntar("¿Nota mínima?");
        Assert.Equal(HttpStatusCode.OK, c);
        var p = Assert.Single(e.Proveedor.Peticiones);
        var esperada = prov == "openai" ? "Bearer llave-distinta-123" : "llave-distinta-123";
        Assert.Equal(esperada, p.Cabecera(prov == "openai" ? "Authorization" : "x-api-key"));
        Assert.DoesNotContain("llave-distinta-123", p.Cuerpo);
    }

    // ---------- Selección de proveedor ----------

    [Theory]
    [InlineData("gemini")]
    [InlineData("azure")]
    [InlineData("openai2")]
    [InlineData("claude")]
    public void T4b_ESC_05_5_valor_desconocido_de_LLM_PROVIDER_la_aplicacion_no_arranca(string prov)
    {
        Assert.ThrowsAny<Exception>(() => { using var e = new EntornoProveedor(prov); });
    }

    [Theory]
    [InlineData("openai", "/chat/completions")]
    [InlineData("OPENAI", "/chat/completions")]
    [InlineData("Anthropic", "/v1/messages")]
    [InlineData("anthropic", "/v1/messages")]
    public async Task T4b_ESC_05_5_proveedor_se_elige_sin_importar_mayusculas(string prov, string sufijo)
    {
        using var e = new EntornoProveedor(prov);
        e.Proveedor.Ok(Texto(prov.ToLowerInvariant(), FinalJson, 1, 1));
        var (c, _) = await e.Preguntar("¿Nota mínima?");
        Assert.Equal(HttpStatusCode.OK, c);
        Assert.EndsWith(sufijo, Assert.Single(e.Proveedor.Peticiones).Url);
    }

    [Fact]
    public async Task T4b_defensa_la_fabrica_de_pruebas_bloquea_la_red_real()
    {
        // Sin proveedor falso ni modelo falso: el manejador por defecto lanza, y la API responde 503 (nunca sale a la red).
        using var f = Fabrica.Crear("llave-falsa");
        using var cliente = f.CreateClient();
        var r = await cliente.PostAsync(EntornoProveedor.Url, new StringContent(
            "{\"pregunta\":\"¿Nota mínima?\",\"usuario\":{\"rol\":\"estudiante\",\"id\":\"T-E1\"}}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.DoesNotContain("llave-falsa", await r.Content.ReadAsStringAsync());
    }
}

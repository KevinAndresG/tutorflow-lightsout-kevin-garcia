using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using TutorFlow.Api.Llm;

namespace TutorFlow.Tests;

/// <summary>
/// Trazabilidad del modelo en la auditoría: modeloPedido, modelosRespondidos, tokensEntrada y tokensSalida se AÑADEN
/// a los 9 campos de RN-10, y la respuesta HTTP conserva exactamente los 5 campos del contrato (SPEC 4).
/// Todo por HTTP contra la API con proveedor falso; sin red.
/// </summary>
public class ModeloRespondidoTests
{
    private const string FinalJson = "{\"respuesta\":\"La nota mínima es 3.0.\",\"citas\":[]}";
    private const string Pregunta = "¿Cuál es la nota mínima modelo-trazado-1?";

    private static string Texto(string prov, string? modelo, int entrada, int salida)
    {
        var o = prov == "openai"
            ? new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = FinalJson }, ["finish_reason"] = "stop" }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = entrada, ["completion_tokens"] = salida },
            }
            : new JsonObject
            {
                ["id"] = "msg_1", ["type"] = "message", ["role"] = "assistant", ["stop_reason"] = "end_turn",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = FinalJson }),
                ["usage"] = new JsonObject { ["input_tokens"] = entrada, ["output_tokens"] = salida },
            };
        if (modelo != null) o["model"] = modelo;
        return o.ToJsonString();
    }

    private static string Herramienta(string prov, string? modelo, int entrada, int salida, string estudianteId)
    {
        var args = "{\"estudianteId\":\"" + estudianteId + "\"}";
        var o = prov == "openai"
            ? new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant", ["content"] = null,
                        ["tool_calls"] = new JsonArray(new JsonObject
                        {
                            ["id"] = "call_1", ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = "consultar_notas", ["arguments"] = args },
                        }),
                    },
                    ["finish_reason"] = "tool_calls",
                }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = entrada, ["completion_tokens"] = salida },
            }
            : new JsonObject
            {
                ["id"] = "msg_t", ["type"] = "message", ["role"] = "assistant", ["stop_reason"] = "tool_use",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_use", ["id"] = "toolu_1", ["name"] = "consultar_notas", ["input"] = JsonNode.Parse(args),
                }),
                ["usage"] = new JsonObject { ["input_tokens"] = entrada, ["output_tokens"] = salida },
            };
        if (modelo != null) o["model"] = modelo;
        return o.ToJsonString();
    }

    private static EntornoAuditoria Entorno(string prov, string? modelo)
    {
        var cfg = new Dictionary<string, string?> { ["LLM_PROVIDER"] = prov };
        if (modelo != null) cfg["LLM_MODEL"] = modelo;
        return new EntornoAuditoria(null, cfg);
    }

    private static string[] Modelos(JsonElement l)
    {
        var a = l.GetProperty("modelosRespondidos");
        Assert.Equal(JsonValueKind.Array, a.ValueKind);
        return a.EnumerateArray().Select(x => { Assert.Equal(JsonValueKind.String, x.ValueKind); return x.GetString()!; }).ToArray();
    }

    private static string? Pedido(JsonElement l) => AuditoriaAsserts.Texto(l, "modeloPedido");

    private static void AssertTokensCoherentes(JsonElement l)
    {
        var ent = l.GetProperty("tokensEntrada");
        var sal = l.GetProperty("tokensSalida");
        Assert.Equal(JsonValueKind.Number, ent.ValueKind);
        Assert.Equal(JsonValueKind.Number, sal.ValueKind);
        Assert.True(ent.GetInt64() >= 0 && sal.GetInt64() >= 0);
        var suma = ent.GetInt64() + sal.GetInt64();
        Assert.Equal(Math.Min(suma, int.MaxValue), AuditoriaAsserts.Tokens(l));
    }

    // ---------- Cliente: LlmRespuesta.Modelo ----------

    [Fact]
    public void ModeloRespondido_LlmRespuesta_tiene_Modelo_opcional_con_valor_por_defecto_null()
    {
        var r = new LlmRespuesta("t", [], new LlmUso(1, 1));
        Assert.Null(r.Modelo);
        var r2 = new LlmRespuesta("t", [], new LlmUso(1, 1), "m-x");
        Assert.Equal("m-x", r2.Modelo);
        Assert.Equal(typeof(string), typeof(LlmRespuesta).GetProperty("Modelo")!.PropertyType);
    }

    // ---------- Modelo respondido del proveedor ----------

    [Theory]
    [InlineData("openai", "gemini-3.5-flash-001")]
    [InlineData("anthropic", "claude-haiku-4-5-20251001")]
    public async Task ModeloRespondido_el_campo_model_del_proveedor_aparece_en_modelosRespondidos(string prov, string respondido)
    {
        using var e = Entorno(prov, null);
        e.Proveedor.Ok(Texto(prov, respondido, 10, 5));
        var (c, _) = await e.Preguntar(Pregunta, "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);
        var l = await e.UnaLinea();
        Assert.Equal([respondido], Modelos(l));
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task ModeloRespondido_proveedor_sin_campo_model_no_agrega_modelo_y_no_rompe(string prov)
    {
        using var e = Entorno(prov, null);
        e.Proveedor.Ok(Texto(prov, null, 10, 5));
        var (c, _) = await e.Preguntar(Pregunta, "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);
        var l = await e.UnaLinea();
        Assert.Empty(Modelos(l));
        Assert.NotNull(Pedido(l));
    }

    // ---------- Modelo pedido ----------

    [Theory]
    [InlineData("openai", "modelo-configurado-x")]
    [InlineData("anthropic", "claude-configurado-y")]
    public async Task ModeloRespondido_LLM_MODEL_configurado_es_el_modeloPedido(string prov, string modelo)
    {
        using var e = Entorno(prov, modelo);
        e.Proveedor.Ok(Texto(prov, modelo, 10, 5));
        await e.Preguntar(Pregunta, "estudiante", "T-E1");
        var l = await e.UnaLinea();
        Assert.Equal(modelo, Pedido(l));
        Assert.Equal(modelo, (string?)e.Proveedor.Peticiones[0].Json["model"]);
    }

    [Theory]
    [InlineData("openai", "gemini-3.5-flash")]
    [InlineData("anthropic", "claude-haiku-4-5")]
    public async Task ModeloRespondido_sin_LLM_MODEL_el_modeloPedido_es_el_valor_por_defecto_del_adaptador(string prov, string porDefecto)
    {
        using var e = Entorno(prov, null);
        e.Proveedor.Ok(Texto(prov, porDefecto, 10, 5));
        await e.Preguntar(Pregunta, "estudiante", "T-E1");
        var l = await e.UnaLinea();
        Assert.Equal(porDefecto, Pedido(l));
    }

    [Theory]
    [InlineData("openai", "gemini-3.5-flash")]
    [InlineData("anthropic", "claude-haiku-4-5")]
    public async Task ModeloRespondido_LLM_MODEL_vacio_equivale_a_no_configurado(string prov, string porDefecto)
    {
        using var e = Entorno(prov, "");
        e.Proveedor.Ok(Texto(prov, porDefecto, 10, 5));
        await e.Preguntar(Pregunta, "estudiante", "T-E1");
        var l = await e.UnaLinea();
        Assert.Equal(porDefecto, Pedido(l));
        Assert.Equal(porDefecto, (string?)e.Proveedor.Peticiones[0].Json["model"]);
    }

    [Fact]
    public async Task ModeloRespondido_proveedor_responde_otro_modelo_la_auditoria_lo_refleja_tal_cual()
    {
        using var e = Entorno("openai", null);
        e.Proveedor.Ok(Texto("openai", "gemini-3.6-flash", 10, 5));
        var (c, _) = await e.Preguntar(Pregunta, "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);
        var l = await e.UnaLinea();
        Assert.Equal("gemini-3.5-flash", Pedido(l));
        Assert.Equal(["gemini-3.6-flash"], Modelos(l));
    }

    // ---------- Varias vueltas ----------

    [Fact]
    public async Task ModeloRespondido_dos_vueltas_con_modelos_distintos_registra_ambos_en_orden()
    {
        using var e = Entorno("openai", null);
        e.Proveedor.Ok(Herramienta("openai", "modelo-a", 10, 5, "T-E1")).Ok(Texto("openai", "modelo-b", 7, 3));
        var (c, _) = await e.Preguntar(Pregunta, "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);
        var l = await e.UnaLinea();
        Assert.Equal(["modelo-a", "modelo-b"], Modelos(l));
        Assert.Equal(25, AuditoriaAsserts.Tokens(l));
        Assert.Equal(17, l.GetProperty("tokensEntrada").GetInt64());
        Assert.Equal(8, l.GetProperty("tokensSalida").GetInt64());
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task ModeloRespondido_dos_vueltas_con_el_mismo_modelo_no_duplica(string prov)
    {
        using var e = Entorno(prov, null);
        e.Proveedor.Ok(Herramienta(prov, "mismo-modelo", 10, 5, "T-E1")).Ok(Texto(prov, "mismo-modelo", 7, 3));
        await e.Preguntar(Pregunta, "estudiante", "T-E1");
        var l = await e.UnaLinea();
        Assert.Equal(["mismo-modelo"], Modelos(l));
        AssertTokensCoherentes(l);
    }

    // ---------- Tokens de entrada y salida ----------

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task ModeloRespondido_tokensEntrada_mas_tokensSalida_es_tokens(string prov)
    {
        using var e = Entorno(prov, null);
        e.Proveedor.Ok(Texto(prov, "m", 123, 45));
        var (c, cuerpo) = await e.Preguntar(Pregunta, "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);
        var l = await e.UnaLinea();
        Assert.Equal(123, l.GetProperty("tokensEntrada").GetInt64());
        Assert.Equal(45, l.GetProperty("tokensSalida").GetInt64());
        AssertTokensCoherentes(l);
        Assert.Equal(168, JsonDocument.Parse(cuerpo).RootElement.GetProperty("tokens").GetInt64());
    }

    // ---------- Sin llamada al modelo ----------

    private static void AssertSinModelo(JsonElement l)
    {
        Assert.Null(Pedido(l));
        Assert.Empty(Modelos(l));
        Assert.Equal(0, AuditoriaAsserts.Tokens(l));
        Assert.Equal(0, l.GetProperty("tokensEntrada").GetInt64());
        Assert.Equal(0, l.GetProperty("tokensSalida").GetInt64());
        Assert.Equal(0, AuditoriaAsserts.Costo(l));
    }

    [Fact]
    public async Task ModeloRespondido_400_antes_del_modelo_no_registra_modelo_ni_tokens()
    {
        using var e = Entorno("openai", "modelo-configurado-x");
        var (c, _) = await e.Preguntar("", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.BadRequest, c);
        var l = await e.UnaLinea();
        AuditoriaAsserts.AssertForma(l);
        Assert.Equal(400, AuditoriaAsserts.Codigo(l));
        AssertSinModelo(l);
        Assert.Equal(0, e.Proveedor.Cuenta);
    }

    [Theory]
    [InlineData("estudiante", "T-NOEXISTE")]
    [InlineData("coordinador", "T-E1")]
    public async Task ModeloRespondido_403_por_usuario_invalido_antes_del_modelo_no_registra_modelo(string rol, string id)
    {
        using var e = Entorno("anthropic", null);
        var (c, _) = await e.Preguntar(Pregunta, rol, id);
        Assert.Equal(HttpStatusCode.Forbidden, c);
        var l = await e.UnaLinea();
        AuditoriaAsserts.AssertForma(l);
        Assert.Equal(403, AuditoriaAsserts.Codigo(l));
        AssertSinModelo(l);
        Assert.Equal(0, e.Proveedor.Cuenta);
    }

    [Fact]
    public async Task ModeloRespondido_403_por_pregunta_sobre_otro_estudiante_antes_del_modelo_no_registra_modelo()
    {
        using var e = Entorno("openai", null);
        var (c, _) = await e.Preguntar("¿Qué notas tiene Bruno Prueba Dos?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.Forbidden, c);
        var l = await e.UnaLinea();
        AssertSinModelo(l);
    }

    // ---------- Fallas tras una vuelta ----------

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task ModeloRespondido_403_por_herramienta_tras_una_vuelta_registra_modelo_y_tokens_de_esa_vuelta(string prov)
    {
        using var e = Entorno(prov, null);
        e.Proveedor.Ok(Herramienta(prov, "modelo-vuelta-1", 40, 9, "T-E2"));   // T-E1 pide a T-E2 (otro programa)
        var (c, _) = await e.Preguntar("¿Cómo voy en mis notas?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.Forbidden, c);
        var l = await e.UnaLinea();
        Assert.Equal(403, AuditoriaAsserts.Codigo(l));
        Assert.Equal(["modelo-vuelta-1"], Modelos(l));
        Assert.NotNull(Pedido(l));
        Assert.Equal(49, AuditoriaAsserts.Tokens(l));
        Assert.Equal(40, l.GetProperty("tokensEntrada").GetInt64());
        Assert.Equal(9, l.GetProperty("tokensSalida").GetInt64());
        Assert.DoesNotContain("T-E2", AuditoriaAsserts.Estudiantes(l));
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task ModeloRespondido_503_en_la_segunda_vuelta_conserva_el_modelo_de_la_primera(string prov)
    {
        using var e = Entorno(prov, null);
        e.Proveedor.Ok(Herramienta(prov, "modelo-vuelta-1", 40, 9, "T-E1"))
                   .Responde(HttpStatusCode.InternalServerError, "{}");
        var (c, _) = await e.Preguntar("¿Cómo voy en mis notas?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, c);
        var l = await e.UnaLinea();
        Assert.Equal(503, AuditoriaAsserts.Codigo(l));
        Assert.Equal(["modelo-vuelta-1"], Modelos(l));
        Assert.NotNull(Pedido(l));
        AssertTokensCoherentes(l);
    }

    // ---------- Contrato y campos previos ----------

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task ModeloRespondido_la_respuesta_200_conserva_exactamente_los_5_campos_del_contrato(string prov)
    {
        using var e = Entorno(prov, null);
        e.Proveedor.Ok(Texto(prov, "m-visible-solo-en-auditoria", 10, 5));
        var r = await e.Cliente.PostAsync(EntornoAuditoria.Url, new StringContent(
            JsonSerializer.Serialize(new { pregunta = Pregunta, usuario = new { rol = "estudiante", id = "T-E1" } }),
            System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var cuerpo = await r.Content.ReadAsStringAsync();
        var campos = JsonDocument.Parse(cuerpo).RootElement.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "citas", "costoUsd", "respuesta", "tokens", "usoDatosEstudiante" }, campos);
        Assert.DoesNotContain("m-visible-solo-en-auditoria", cuerpo);
        foreach (var h in r.Headers.Concat(r.Content.Headers))
        {
            Assert.DoesNotContain("model", h.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("m-visible-solo-en-auditoria", string.Join(",", h.Value));
        }
    }

    [Fact]
    public async Task ModeloRespondido_los_9_campos_de_RN_10_siguen_intactos_junto_a_los_nuevos()
    {
        using var e = Entorno("openai", null);
        e.Proveedor.Ok(Texto("openai", "m", 10, 5));
        await e.Preguntar(Pregunta, "estudiante", "T-E1");
        var l = await e.UnaLinea();
        AuditoriaAsserts.AssertForma(l);
        foreach (var n in new[] { "modeloPedido", "modelosRespondidos", "tokensEntrada", "tokensSalida" })
            Assert.True(l.TryGetProperty(n, out _), "falta " + n);
        Assert.Equal("estudiante", AuditoriaAsserts.Texto(l, "rol"));
        Assert.Equal("T-E1", AuditoriaAsserts.Texto(l, "id"));
        Assert.Equal(Pregunta, AuditoriaAsserts.Texto(l, "pregunta"));
        Assert.Equal(200, AuditoriaAsserts.Codigo(l));
    }
}

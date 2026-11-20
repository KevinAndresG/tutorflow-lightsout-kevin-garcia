using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TutorFlow.Tests;

/// <summary>
/// Tokens de razonamiento (Gemini 3, adaptador OpenAI-compatible): el proveedor los reporta solo en total_tokens.
/// TokensSalida = max(completion_tokens, total_tokens - prompt_tokens) si total_tokens es válido y consistente.
/// Todo por HTTP con proveedor falso; sin red.
/// </summary>
public class TokensRazonamientoTests
{
    private const string FinalJson = "{\"respuesta\":\"La nota mínima es 3.0.\",\"citas\":[]}";
    private const string Pregunta = "¿Cuál es la nota mínima razonamiento-1?";
    private const double PE = 0.5, PS = 3;

    private static string UsoOpenAi(string usoJson, bool herramienta, string estudianteId = "T-E1")
    {
        var msg = herramienta
            ? "{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"consultar_notas\",\"arguments\":\"{\\\"estudianteId\\\":\\\"" + estudianteId + "\\\"}\"}}]}"
            : "{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(FinalJson) + "}";
        var fin = herramienta ? "tool_calls" : "stop";
        return "{\"model\":\"gemini-3.5-flash\",\"choices\":[{\"message\":" + msg + ",\"finish_reason\":\"" + fin + "\"}],\"usage\":" + usoJson + "}";
    }

    private static string Uso(object? p, object? c, object? t, string extra = "")
    {
        string V(object? v) => v switch { null => "null", string s => JsonSerializer.Serialize(s), _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)! };
        var partes = new List<string>();
        if (p is not Falta) partes.Add("\"prompt_tokens\":" + V(p));
        if (c is not Falta) partes.Add("\"completion_tokens\":" + V(c));
        if (t is not Falta) partes.Add("\"total_tokens\":" + V(t));
        if (extra.Length > 0) partes.Add(extra);
        return "{" + string.Join(",", partes) + "}";
    }

    private sealed class Falta { public static readonly Falta X = new(); }

    private static EntornoAuditoria Entorno() => new(null, new Dictionary<string, string?>
    {
        ["LLM_PROVIDER"] = "openai",
        ["LLM_PRECIO_ENTRADA_MTOK"] = "0.5",
        ["LLM_PRECIO_SALIDA_MTOK"] = "3",
    });

    private static void AssertCoherente(JsonElement l, long entrada, long salida)
    {
        Assert.Equal(entrada, l.GetProperty("tokensEntrada").GetInt64());
        Assert.Equal(salida, l.GetProperty("tokensSalida").GetInt64());
        Assert.Equal(entrada + salida, AuditoriaAsserts.Tokens(l));
        Assert.Equal(AuditoriaAsserts.CostoEsperado(entrada, salida, PE, PS), AuditoriaAsserts.Costo(l), 9);
    }

    private static async Task Ejecuta(string uso, long entrada, long salida)
    {
        using var e = Entorno();
        e.Proveedor.Ok(UsoOpenAi(uso, false));
        var (c, cuerpo) = await e.Preguntar(Pregunta, "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);
        var r = JsonDocument.Parse(cuerpo).RootElement;
        Assert.Equal(entrada + salida, r.GetProperty("tokens").GetInt64());
        Assert.Equal(AuditoriaAsserts.CostoEsperado(entrada, salida, PE, PS), r.GetProperty("costoUsd").GetDouble(), 9);
        AssertCoherente(await e.UnaLinea(), entrada, salida);
    }

    [Fact]
    public Task ESC_T6f_1_gemini3_razonamiento_solo_en_total_tokens_se_cuenta_como_salida() =>
        Ejecuta(Uso(70, 23, 172), 70, 102);

    [Fact]
    public async Task ESC_T6f_2_dos_vueltas_con_razonamiento_distinto_suman_ambas()
    {
        using var e = Entorno();
        e.Proveedor.Ok(UsoOpenAi(Uso(70, 23, 172), true)).Ok(UsoOpenAi(Uso(100, 10, 150), false));
        var (c, cuerpo) = await e.Preguntar("¿Cómo voy en mis notas?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);
        // Vuelta 1: 70 / 102. Vuelta 2: 100 / 50.
        Assert.Equal(322, JsonDocument.Parse(cuerpo).RootElement.GetProperty("tokens").GetInt64());
        AssertCoherente(await e.UnaLinea(), 170, 152);
    }

    [Fact]
    public Task ESC_T6f_3_sin_total_tokens_se_usa_completion_tokens() =>
        Ejecuta(Uso(70, 23, Falta.X), 70, 23);

    [Theory]
    [InlineData(50)]   // menor que prompt + completion
    [InlineData(93)]   // exactamente igual: sin diferencia
    [InlineData(10)]   // menor que prompt
    public Task ESC_T6f_4_total_tokens_inconsistente_se_ignora(int total) =>
        Ejecuta(Uso(70, 23, total), 70, 23);

    [Theory]
    [InlineData("-5")]
    [InlineData("-9223372036854775808")]
    [InlineData("\"abc\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("[1]")]
    [InlineData("{}")]
    public async Task ESC_T6f_5a_total_tokens_invalido_no_da_500_ni_negativos(string total)
    {
        using var e = Entorno();
        e.Proveedor.Ok(UsoOpenAi("{\"prompt_tokens\":70,\"completion_tokens\":23,\"total_tokens\":" + total + "}", false));
        var (c, cuerpo) = await e.Preguntar(Pregunta, "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);
        Assert.Equal(93, JsonDocument.Parse(cuerpo).RootElement.GetProperty("tokens").GetInt64());
        AssertCoherente(await e.UnaLinea(), 70, 23);
    }

    [Theory]
    [InlineData("9223372036854775807")]
    [InlineData("3000000000")]
    [InlineData("2147483648")]
    [InlineData("1e30")]
    public async Task ESC_T6f_5b_total_tokens_enorme_no_desborda_ni_da_500(string total)
    {
        using var e = Entorno();
        e.Proveedor.Ok(UsoOpenAi("{\"prompt_tokens\":70,\"completion_tokens\":23,\"total_tokens\":" + total + "}", false));
        var (c, cuerpo) = await e.Preguntar(Pregunta, "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);
        var t = JsonDocument.Parse(cuerpo).RootElement.GetProperty("tokens").GetInt64();
        Assert.InRange(t, 0, int.MaxValue);
        var costo = JsonDocument.Parse(cuerpo).RootElement.GetProperty("costoUsd").GetDouble();
        Assert.True(costo >= 0 && double.IsFinite(costo));
        var l = await e.UnaLinea();
        AuditoriaAsserts.AssertForma(l);
        Assert.InRange(AuditoriaAsserts.Tokens(l), 0, int.MaxValue);
        Assert.InRange(l.GetProperty("tokensEntrada").GetInt64(), 0, int.MaxValue);
        Assert.InRange(l.GetProperty("tokensSalida").GetInt64(), 0, int.MaxValue);
        Assert.True(AuditoriaAsserts.Costo(l) >= 0 && double.IsFinite(AuditoriaAsserts.Costo(l)));
    }

    [Fact]
    public async Task ESC_T6f_5c_total_enorme_en_una_vuelta_y_otra_normal_no_desborda_la_suma()
    {
        using var e = Entorno();
        e.Proveedor.Ok(UsoOpenAi(Uso(70, 23, 9223372036854775807), true)).Ok(UsoOpenAi(Uso(70, 23, 9223372036854775807), false));
        var (c, cuerpo) = await e.Preguntar("¿Cómo voy en mis notas?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);
        Assert.InRange(JsonDocument.Parse(cuerpo).RootElement.GetProperty("tokens").GetInt64(), 0, int.MaxValue);
        var l = await e.UnaLinea();
        Assert.InRange(AuditoriaAsserts.Tokens(l), 0, int.MaxValue);
    }

    [Fact]
    public Task ESC_T6f_6a_reasoning_tokens_presente_con_total_consistente_no_se_cuenta_dos_veces() =>
        Ejecuta(Uso(70, 23, 172, "\"completion_tokens_details\":{\"reasoning_tokens\":79}"), 70, 102);

    [Fact]
    public Task ESC_T6f_6b_completion_que_ya_incluye_razonamiento_no_se_suma_otra_vez() =>
        Ejecuta(Uso(10, 50, 60, "\"completion_tokens_details\":{\"reasoning_tokens\":30}"), 10, 50);

    [Fact]
    public async Task ESC_T6f_7_403_por_herramienta_tras_vuelta_con_razonamiento_audita_tokens_con_razonamiento()
    {
        using var e = Entorno();
        e.Proveedor.Ok(UsoOpenAi(Uso(70, 23, 172), true, "T-E2"));   // T-E1 pide a T-E2 (otro programa)
        var (c, _) = await e.Preguntar("¿Cómo voy en mis notas?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.Forbidden, c);
        var l = await e.UnaLinea();
        Assert.Equal(403, AuditoriaAsserts.Codigo(l));
        AssertCoherente(l, 70, 102);
        Assert.DoesNotContain("T-E2", AuditoriaAsserts.Estudiantes(l));
    }

    [Fact]
    public async Task ESC_T6f_8_anthropic_no_cambia_output_tokens_ya_incluye_razonamiento()
    {
        using var e = new EntornoAuditoria(null, new Dictionary<string, string?>
        {
            ["LLM_PROVIDER"] = "anthropic",
            ["LLM_PRECIO_ENTRADA_MTOK"] = "0.5",
            ["LLM_PRECIO_SALIDA_MTOK"] = "3",
        });
        var o = new JsonObject
        {
            ["id"] = "msg_1", ["type"] = "message", ["role"] = "assistant", ["stop_reason"] = "end_turn", ["model"] = "claude-haiku-4-5",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = FinalJson }),
            // total_tokens no existe en Anthropic; si apareciera, debe ignorarse.
            ["usage"] = new JsonObject { ["input_tokens"] = 70, ["output_tokens"] = 23, ["total_tokens"] = 172 },
        };
        e.Proveedor.Ok(o.ToJsonString());
        var (c, cuerpo) = await e.Preguntar(Pregunta, "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);
        Assert.Equal(93, JsonDocument.Parse(cuerpo).RootElement.GetProperty("tokens").GetInt64());
        AssertCoherente(await e.UnaLinea(), 70, 23);
        var campos = JsonDocument.Parse(cuerpo).RootElement.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "citas", "costoUsd", "respuesta", "tokens", "usoDatosEstudiante" }, campos);
    }
}

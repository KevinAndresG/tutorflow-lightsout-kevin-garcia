using System.Net;
using System.Text.Json;
using static TutorFlow.Tests.AuditoriaAsserts;

namespace TutorFlow.Tests;

/// <summary>
/// ESC-06.1..06.5, ESC-02.1, ESC-02.5/04.6 y ESC-03.1 (parte de auditoría): RN-10, RN-11, RN-12, RN-13, CA-12, CA-13.
/// Fixtures: T-E1/T-E3 Programa Alfa (coordina T-C1), T-E2/T-E4/T-E5 Programa Beta (coordina T-C2).
/// </summary>
public class AuditoriaTests
{
    private const string Cita = "{\"documento\":\"reglamento-prueba.md\",\"seccion\":\"Nota mínima\"}";
    private static string Id(string id) => "{\"estudianteId\":\"" + id + "\"}";

    private static IDictionary<string, string?> Precios(string entrada, string salida) => new Dictionary<string, string?>
    {
        ["LLM_PRECIO_ENTRADA_MTOK"] = entrada,
        ["LLM_PRECIO_SALIDA_MTOK"] = salida,
    };

    // ---------- ESC-06.1: toda petición queda registrada, con todos los campos ----------

    [Fact]
    public async Task ESC_06_1_peticion_200_una_linea_con_todos_los_campos_tipos_y_citas_devueltas()
    {
        var llm = new FakeLlmClient().Final("Es 3.0.", "[" + Cita + ",{\"documento\":\"reglamento-prueba.md\",\"seccion\":\"Inventada\"}]", 11, 4);
        using var e = new EntornoAuditoria(llm, Precios("2", "10"));

        var (c, cuerpo) = await e.Preguntar("¿Cuál es la nota mínima pregunta-200?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);

        var l = await e.UnaLinea();
        AssertForma(l);
        Assert.Equal(200, Codigo(l));
        Assert.Equal("estudiante", Texto(l, "rol"));
        Assert.Equal("T-E1", Texto(l, "id"));
        Assert.Equal("¿Cuál es la nota mínima pregunta-200?", Texto(l, "pregunta"));
        Assert.Empty(Estudiantes(l));
        var citas = l.GetProperty("citas");
        Assert.Equal(1, citas.GetArrayLength());   // solo la realmente devuelta (la inventada se descartó)
        Assert.Equal("reglamento-prueba.md", citas[0].GetProperty("documento").GetString());
        Assert.Equal("Nota mínima", citas[0].GetProperty("seccion").GetString());

        // La auditoría coincide con la respuesta HTTP.
        using var resp = JsonDocument.Parse(cuerpo);
        Assert.Equal(resp.RootElement.GetProperty("tokens").GetInt64(), Tokens(l));
        Assert.Equal(15, Tokens(l));
        Assert.Equal(resp.RootElement.GetProperty("costoUsd").GetDouble(), Costo(l), 12);
        Assert.Equal(CostoEsperado(11, 4, 2, 10), Costo(l), 9);
        Assert.Equal(resp.RootElement.GetProperty("citas").GetRawText(), citas.GetRawText());
    }

    [Fact]
    public async Task ESC_06_1_peticion_200_tambien_se_emite_por_ILogger()
    {
        using var e = new EntornoAuditoria(new FakeLlmClient().Final("Es 3.0.", "[]", 1, 1));
        var (c, _) = await e.Preguntar("¿Cuál es la nota mínima marca-logger-9921?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);
        await e.UnaLinea();
        Assert.Contains("marca-logger-9921", e.Logs.Todo);
    }

    [Fact]
    public async Task ESC_06_1_peticion_400_pregunta_vacia_queda_registrada()
    {
        using var e = new EntornoAuditoria(new FakeLlmClient());
        var (c, _) = await e.Preguntar("", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.BadRequest, c);

        var l = await e.UnaLinea();
        AssertForma(l);
        Assert.Equal(400, Codigo(l));
        Assert.Equal("estudiante", Texto(l, "rol"));
        Assert.Equal("T-E1", Texto(l, "id"));
        Assert.True(Texto(l, "pregunta") is null or "");
        Assert.Empty(Estudiantes(l));
        Assert.Equal(0, l.GetProperty("citas").GetArrayLength());
        Assert.Equal(0, Tokens(l));
        Assert.Equal(0, Costo(l));
    }

    [Fact]
    public async Task ESC_06_1_peticion_400_rol_invalido_registra_el_rol_recibido_o_null()
    {
        using var e = new EntornoAuditoria(new FakeLlmClient());
        var (c, _) = await e.Preguntar("¿Nota mínima?", "administrador", "T-E1");
        Assert.Equal(HttpStatusCode.BadRequest, c);
        var l = await e.UnaLinea();
        AssertForma(l);
        Assert.Equal(400, Codigo(l));
        Assert.Equal("¿Nota mínima?", Texto(l, "pregunta"));
        Assert.True(Texto(l, "rol") is null or "administrador");
        Assert.Equal(0, Tokens(l));
        Assert.Equal(0, Costo(l));
    }

    [Fact]
    public async Task ESC_06_1_peticion_400_json_invalido_queda_registrada_con_campos_null()
    {
        using var e = new EntornoAuditoria(new FakeLlmClient());
        var (c, _) = await e.Raw("{esto no es json");
        Assert.Equal(HttpStatusCode.BadRequest, c);

        var l = await e.UnaLinea();
        AssertForma(l);
        Assert.Equal(400, Codigo(l));
        Assert.Null(Texto(l, "rol"));
        Assert.Null(Texto(l, "id"));
        Assert.Null(Texto(l, "pregunta"));
        Assert.Empty(Estudiantes(l));
        Assert.Equal(0, Tokens(l));
        Assert.Equal(0, Costo(l));
    }

    [Fact]
    public async Task ESC_06_1_peticion_400_cuerpo_sin_usuario_queda_registrada()
    {
        using var e = new EntornoAuditoria(new FakeLlmClient());
        var (c, _) = await e.Raw("{\"pregunta\":\"¿Nota mínima sin-usuario?\"}");
        Assert.Equal(HttpStatusCode.BadRequest, c);

        var l = await e.UnaLinea();
        AssertForma(l);
        Assert.Equal(400, Codigo(l));
        Assert.Null(Texto(l, "rol"));
        Assert.Null(Texto(l, "id"));
        Assert.True(Texto(l, "pregunta") is null or "¿Nota mínima sin-usuario?");
        Assert.Equal(0, Tokens(l));
    }

    [Fact]
    public async Task ESC_06_1_peticion_400_pregunta_de_mas_de_1000_caracteres_registrada()
    {
        using var e = new EntornoAuditoria(new FakeLlmClient());
        var (c, _) = await e.Preguntar(new string('a', 1001), "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.BadRequest, c);
        var l = await e.UnaLinea();
        AssertForma(l);
        Assert.Equal(400, Codigo(l));
        Assert.Equal(0, Tokens(l));
        Assert.Equal(0, Costo(l));
    }

    [Fact]
    public async Task ESC_06_1_peticion_403_por_filtro_de_terceros_queda_registrada()
    {
        using var e = new EntornoAuditoria(new FakeLlmClient());
        var (c, _) = await e.Preguntar("¿Qué notas tiene Bruno Prueba Dos?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.Forbidden, c);

        var l = await e.UnaLinea();
        AssertForma(l);
        Assert.Equal(403, Codigo(l));
        Assert.Equal("estudiante", Texto(l, "rol"));
        Assert.Equal("T-E1", Texto(l, "id"));
        Assert.Equal("¿Qué notas tiene Bruno Prueba Dos?", Texto(l, "pregunta"));
        Assert.Empty(Estudiantes(l));
        Assert.Equal(0, l.GetProperty("citas").GetArrayLength());
        Assert.Equal(0, Tokens(l));
        Assert.Equal(0, Costo(l));
    }

    [Fact]
    public async Task ESC_06_1_peticion_403_usuario_inexistente_queda_registrada()
    {
        using var e = new EntornoAuditoria(new FakeLlmClient());
        var (c, _) = await e.Preguntar("¿Nota mínima?", "estudiante", "NO-EXISTE");
        Assert.Equal(HttpStatusCode.Forbidden, c);
        var l = await e.UnaLinea();
        AssertForma(l);
        Assert.Equal(403, Codigo(l));
        Assert.Equal("estudiante", Texto(l, "rol"));
        Assert.Equal("NO-EXISTE", Texto(l, "id"));
        Assert.Empty(Estudiantes(l));
        Assert.Equal(0, Tokens(l));
        Assert.Equal(0, Costo(l));
    }

    [Fact]
    public async Task ESC_06_1_peticion_403_rol_cruzado_queda_registrada()
    {
        using var e = new EntornoAuditoria(new FakeLlmClient());
        var (c, _) = await e.Preguntar("¿Nota mínima?", "coordinador", "T-E1");
        Assert.Equal(HttpStatusCode.Forbidden, c);
        var l = await e.UnaLinea();
        AssertForma(l);
        Assert.Equal(403, Codigo(l));
        Assert.Equal("coordinador", Texto(l, "rol"));
        Assert.Equal("T-E1", Texto(l, "id"));
        Assert.Equal(0, Tokens(l));
    }

    [Fact]
    public async Task ESC_06_1_peticion_403_por_herramienta_queda_registrada()
    {
        var llm = new FakeLlmClient().Herramienta("consultar_notas", Id("T-E2"), 10, 1);
        using var e = new EntornoAuditoria(llm);
        var (c, _) = await e.Preguntar("¿Cómo voy en mis notas?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.Forbidden, c);
        var l = await e.UnaLinea();
        AssertForma(l);
        Assert.Equal(403, Codigo(l));
        Assert.Equal("T-E1", Texto(l, "id"));
        Assert.Empty(Estudiantes(l));
    }

    [Fact]
    public async Task ESC_06_1_peticion_503_sin_llave_queda_registrada_con_tokens_y_costo_cero()
    {
        var llm = new FakeLlmClient();
        using var e = new EntornoAuditoria(llm, llave: "");
        var (c, _) = await e.Preguntar("¿Nota mínima?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, c);
        var l = await e.UnaLinea();
        AssertForma(l);
        Assert.Equal(503, Codigo(l));
        Assert.Equal("estudiante", Texto(l, "rol"));
        Assert.Equal("T-E1", Texto(l, "id"));
        Assert.Equal("¿Nota mínima?", Texto(l, "pregunta"));
        Assert.Empty(Estudiantes(l));
        Assert.Equal(0, Tokens(l));
        Assert.Equal(0, Costo(l));
        Assert.Equal(0, llm.Llamadas);
    }

    [Fact]
    public async Task ESC_06_1_peticion_503_proveedor_caido_en_la_primera_vuelta_queda_registrada()
    {
        var llm = new FakeLlmClient().Lanza(new HttpRequestException("caído"));
        using var e = new EntornoAuditoria(llm);
        var (c, _) = await e.Preguntar("¿Nota mínima?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, c);
        var l = await e.UnaLinea();
        AssertForma(l);
        Assert.Equal(503, Codigo(l));
        Assert.Equal(0, Tokens(l));
        Assert.Equal(0, Costo(l));
    }

    [Fact]
    public async Task ESC_06_1_varias_peticiones_en_secuencia_una_linea_por_cada_una_en_orden()
    {
        var llm = new FakeLlmClient().Final("Uno.", "[]", 1, 1);
        using var e = new EntornoAuditoria(llm);
        await e.Preguntar("primera-pregunta", "estudiante", "T-E1");
        await e.Preguntar("", "estudiante", "T-E1");
        await e.Preguntar("tercera-pregunta", "estudiante", "NO-EXISTE");
        var lineas = await e.LineasCrudas(3);
        Assert.Equal(3, lineas.Count);
        var codigos = lineas.Select(x => Codigo(JsonDocument.Parse(x).RootElement)).ToArray();
        Assert.Equal([200, 400, 403], codigos);
    }

    // ---------- ESC-06.1 / concurrencia ----------

    [Fact]
    public async Task ESC_06_1_veinte_peticiones_concurrentes_producen_veinte_lineas_json_completas_sin_entrelazar()
    {
        var llm = new LlmFuncion((_, _) => Task.FromResult(new TutorFlow.Api.Llm.LlmRespuesta(
            "{\"respuesta\":\"ok\",\"citas\":[" + Cita + "]}", [], new TutorFlow.Api.Llm.LlmUso(7, 3))));
        using var e = new EntornoAuditoria(llm);

        var tareas = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            // 15 normales, 5 rechazadas (403 por usuario inexistente)
            var id = i % 4 == 3 ? "NO-EXISTE" : "T-E1";
            var (c, _) = await e.Preguntar("pregunta-concurrente-" + i + " " + new string('x', 200), "estudiante", id);
            return (i, c);
        })).ToArray();
        var resultados = await Task.WhenAll(tareas);
        Assert.Equal(5, resultados.Count(r => r.c == HttpStatusCode.Forbidden));
        Assert.Equal(15, resultados.Count(r => r.c == HttpStatusCode.OK));

        var lineas = await e.LineasCrudas(20);
        Assert.Equal(20, lineas.Count);
        var vistas = new HashSet<int>();
        foreach (var texto in lineas)
        {
            var l = JsonDocument.Parse(texto).RootElement;   // lanza si la línea está truncada o entrelazada
            AssertForma(l);
            var p = Texto(l, "pregunta")!;
            var i = int.Parse(p.Split(' ')[0].Replace("pregunta-concurrente-", ""));
            Assert.True(vistas.Add(i), "pregunta duplicada " + i);
            Assert.Equal(i % 4 == 3 ? 403 : 200, Codigo(l));
            Assert.Equal(i % 4 == 3 ? 0 : 10, Tokens(l));
        }
        Assert.Equal(20, vistas.Count);
    }

    // ---------- ESC-06.2 / ESC-02.1 / ESC-02.5 / ESC-04.6: la auditoría dice la verdad sobre los datos ----------

    [Fact]
    public async Task ESC_06_2_coordinadora_consulta_asistencia_de_un_estudiante_de_su_programa_aparece_solo_ese_id()
    {
        var llm = new FakeLlmClient()
            .Herramienta("consultar_asistencia", Id("T-E3"), 20, 2)
            .Final("Tiene 5 inasistencias.", "[]", 30, 5);
        using var e = new EntornoAuditoria(llm);
        var (c, cuerpo) = await e.Preguntar("¿Cómo va la asistencia de T-E3?", "coordinador", "T-C1");
        Assert.Equal(HttpStatusCode.OK, c);
        Assert.True(JsonDocument.Parse(cuerpo).RootElement.GetProperty("usoDatosEstudiante").GetBoolean());

        var l = await e.UnaLinea();
        AssertForma(l);
        Assert.Equal(200, Codigo(l));
        Assert.Equal("coordinador", Texto(l, "rol"));
        Assert.Equal("T-C1", Texto(l, "id"));
        Assert.Equal(["T-E3"], Estudiantes(l));
    }

    [Fact]
    public async Task ESC_06_2_dos_herramientas_sobre_el_mismo_estudiante_lo_registran_una_sola_vez()
    {
        var llm = new FakeLlmClient()
            .Paso(_ => new TutorFlow.Api.Llm.LlmRespuesta(null,
            [
                new("a", "consultar_notas", Id("T-E3")),
                new("b", "consultar_asistencia", Id("T-E3")),
            ], new TutorFlow.Api.Llm.LlmUso(5, 5)))
            .Herramienta("consultar_notas", Id("T-E3"), 5, 5)
            .Final("Listo.", "[]", 5, 5);
        using var e = new EntornoAuditoria(llm);
        var (c, _) = await e.Preguntar("Notas y asistencia de T-E3", "coordinador", "T-C1");
        Assert.Equal(HttpStatusCode.OK, c);
        var l = await e.UnaLinea();
        Assert.Equal(["T-E3"], Estudiantes(l));
    }

    [Fact]
    public async Task ESC_06_2_listar_estudiantes_programa_registra_solo_los_de_su_programa()
    {
        var llm = new FakeLlmClient().Herramienta("listar_estudiantes_programa", "{}", 10, 1).Final("Son dos.", "[]", 10, 1);
        using var e = new EntornoAuditoria(llm);
        var (c, _) = await e.Preguntar("¿Qué estudiantes tengo?", "coordinador", "T-C1");
        Assert.Equal(HttpStatusCode.OK, c);
        var l = await e.UnaLinea();
        var ids = Estudiantes(l);
        Assert.Equal(["T-E1", "T-E3"], ids.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        foreach (var ajeno in new[] { "T-E2", "T-E4", "T-E5" }) Assert.DoesNotContain(ajeno, ids);
    }

    [Fact]
    public async Task ESC_06_2_listar_para_la_coordinadora_del_otro_programa_no_incluye_ids_del_primero()
    {
        var llm = new FakeLlmClient().Herramienta("listar_estudiantes_programa", "{}", 10, 1).Final("Son tres.", "[]", 10, 1);
        using var e = new EntornoAuditoria(llm);
        var (c, _) = await e.Preguntar("¿Qué estudiantes tengo?", "coordinador", "T-C2");
        Assert.Equal(HttpStatusCode.OK, c);
        var ids = Estudiantes(await e.UnaLinea());
        Assert.Equal(["T-E2", "T-E4", "T-E5"], ids.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("T-E1", ids);
        Assert.DoesNotContain("T-E3", ids);
    }

    [Fact]
    public async Task ESC_02_1_estudiante_consulta_sus_notas_y_asistencia_estudiantes_consultados_es_su_id()
    {
        var llm = new FakeLlmClient()
            .Herramienta("consultar_notas", Id("T-E1"), 10, 1)
            .Herramienta("consultar_asistencia", Id("T-E1"), 10, 1)
            .Final("Todo bien.", "[]", 10, 1);
        using var e = new EntornoAuditoria(llm);
        var (c, cuerpo) = await e.Preguntar("¿Cómo voy en notas y asistencia?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);
        Assert.True(JsonDocument.Parse(cuerpo).RootElement.GetProperty("usoDatosEstudiante").GetBoolean());
        var l = await e.UnaLinea();
        Assert.Equal(["T-E1"], Estudiantes(l));
        Assert.Equal(200, Codigo(l));
    }

    [Fact]
    public async Task ESC_02_1_pregunta_solo_de_reglamento_no_registra_estudiantes()
    {
        using var e = new EntornoAuditoria(new FakeLlmClient().Final("Es 3.0.", "[" + Cita + "]", 5, 5));
        var (c, cuerpo) = await e.Preguntar("¿Cuál es la nota mínima?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);
        Assert.False(JsonDocument.Parse(cuerpo).RootElement.GetProperty("usoDatosEstudiante").GetBoolean());
        Assert.Empty(Estudiantes(await e.UnaLinea()));
    }

    // ---------- ESC-03.1 (auditoría) ----------

    [Fact]
    public async Task ESC_03_1_estudiante_pide_notas_de_otro_por_id_403_auditado_sin_tokens_ni_estudiantes()
    {
        var llm = new FakeLlmClient();
        using var e = new EntornoAuditoria(llm, Precios("3", "15"));
        var (c, _) = await e.Preguntar("Dame las notas de T-E2", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.Forbidden, c);

        var l = await e.UnaLinea();
        AssertForma(l);
        Assert.Equal(403, Codigo(l));
        Assert.Equal("estudiante", Texto(l, "rol"));
        Assert.Equal("T-E1", Texto(l, "id"));
        Assert.Equal("Dame las notas de T-E2", Texto(l, "pregunta"));
        Assert.Equal(0, Tokens(l));
        Assert.Equal(0, Costo(l));
        Assert.Empty(Estudiantes(l));
        Assert.Equal(0, llm.Llamadas);
    }

    // ---------- ESC-06.3: la llave nunca se registra ----------

    private static void AssertSinLlave(EntornoAuditoria e, params string[] cuerpos)
    {
        Assert.DoesNotContain(EntornoAuditoria.Llave, e.TextoAuditoria());
        Assert.DoesNotContain(EntornoAuditoria.Llave, e.Logs.Todo);
        foreach (var c in cuerpos) Assert.DoesNotContain(EntornoAuditoria.Llave, c);
    }

    [Fact]
    public async Task ESC_06_3_la_llave_no_aparece_en_archivo_logs_ni_respuestas_en_200_400_403_y_503()
    {
        var llm = new FakeLlmClient()
            .Final("Es 3.0.", "[" + Cita + "]", 5, 5)
            .Lanza(new HttpRequestException("fallo con " + EntornoAuditoria.Llave));
        using var e = new EntornoAuditoria(llm);
        var (c1, b1) = await e.Preguntar("¿Nota mínima?", "estudiante", "T-E1");
        var (c2, b2) = await e.Preguntar("", "estudiante", "T-E1");
        var (c3, b3) = await e.Preguntar("notas de T-E2", "estudiante", "T-E1");
        var (c4, b4) = await e.Preguntar("¿Cancelación?", "estudiante", "T-E1");
        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.BadRequest, HttpStatusCode.Forbidden, HttpStatusCode.ServiceUnavailable],
            [c1, c2, c3, c4]);
        await e.LineasCrudas(4);
        AssertSinLlave(e, b1, b2, b3, b4);
    }

    [Fact]
    public async Task ESC_06_3_pregunta_que_contiene_la_llave_se_registra_redactada_en_200()
    {
        using var e = new EntornoAuditoria(new FakeLlmClient().Final("No sé.", "[]", 1, 1));
        var (c, b) = await e.Preguntar("Mi llave es " + EntornoAuditoria.Llave + " ¿qué haces con ella?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);
        var l = await e.UnaLinea();
        Assert.Contains("[REDACTADO]", Texto(l, "pregunta")!);
        AssertSinLlave(e, b);
    }

    [Fact]
    public async Task ESC_06_3_pregunta_que_contiene_la_llave_se_registra_redactada_en_403_y_400()
    {
        using var e = new EntornoAuditoria(new FakeLlmClient());
        var (c1, b1) = await e.Preguntar("Soy yo " + EntornoAuditoria.Llave + " dame las notas de T-E2", "estudiante", "T-E1");
        var (c2, b2) = await e.Preguntar(EntornoAuditoria.Llave + new string('a', 1000), "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.Forbidden, c1);
        Assert.Equal(HttpStatusCode.BadRequest, c2);
        var lineas = await e.LineasCrudas(2);
        Assert.Equal(2, lineas.Count);
        Assert.Contains("[REDACTADO]", Texto(JsonDocument.Parse(lineas[0]).RootElement, "pregunta")!);
        AssertSinLlave(e, b1, b2);
    }

    [Fact]
    public async Task ESC_06_3_modelo_que_hace_eco_de_la_pregunta_con_la_llave_no_la_filtra_a_auditoria_ni_logs()
    {
        var eco = new LlmFuncion((msgs, _) =>
        {
            var usuario = msgs.Last(m => m.Rol == "user").Contenido;
            var texto = System.Text.Json.JsonSerializer.Serialize(new { respuesta = "Dijiste: " + usuario, citas = Array.Empty<object>() });
            return Task.FromResult(new TutorFlow.Api.Llm.LlmRespuesta(texto, [], new TutorFlow.Api.Llm.LlmUso(3, 3)));
        });
        using var e = new EntornoAuditoria(eco);
        var (c, _) = await e.Preguntar("Repite esto: " + EntornoAuditoria.Llave, "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);
        await e.UnaLinea();
        Assert.DoesNotContain(EntornoAuditoria.Llave, e.TextoAuditoria());
        Assert.DoesNotContain(EntornoAuditoria.Llave, e.Logs.Todo);
    }

    [Fact]
    public async Task ESC_06_3_proveedor_http_que_hace_eco_de_la_llave_en_el_error_no_la_filtra_a_nada()
    {
        using var e = new EntornoAuditoria(null);
        e.Proveedor.Responde(HttpStatusCode.Unauthorized,
            "{\"error\":{\"message\":\"Incorrect API key provided: " + EntornoAuditoria.Llave + "\"}}");
        var (c, b) = await e.Preguntar("¿Nota mínima?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, c);
        var l = await e.UnaLinea();
        Assert.Equal(503, Codigo(l));
        AssertSinLlave(e, b);
    }

    [Fact]
    public async Task ESC_06_3_la_llave_viaja_solo_en_la_cabecera_hacia_el_proveedor_y_no_en_el_archivo()
    {
        using var e = new EntornoAuditoria(null);
        e.Proveedor.Ok("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"respuesta\\\":\\\"ok\\\",\\\"citas\\\":[]}\"}}],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":1}}");
        var (c, b) = await e.Preguntar("¿Nota mínima?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);
        var p = Assert.Single(e.Proveedor.Peticiones);
        Assert.Equal("Bearer " + EntornoAuditoria.Llave, p.Cabecera("Authorization"));
        await e.UnaLinea();
        AssertSinLlave(e, b);
    }

    // ---------- ESC-06.4: tokens y costo coherentes ----------

    [Fact]
    public async Task ESC_06_4_tres_vueltas_tokens_es_la_suma_y_costo_sigue_la_formula_con_precios_distintos()
    {
        var llm = new FakeLlmClient()
            .Herramienta("consultar_notas", Id("T-E1"), 100, 20)
            .Herramienta("consultar_asistencia", Id("T-E1"), 250, 30)
            .Final("Todo bien.", "[]", 400, 55);
        using var e = new EntornoAuditoria(llm, Precios("3", "15"));
        var (c, cuerpo) = await e.Preguntar("¿Cómo voy?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);

        const long entrada = 100 + 250 + 400, salida = 20 + 30 + 55;
        var esperado = CostoEsperado(entrada, salida, 3, 15);   // 0.003825
        using var doc = JsonDocument.Parse(cuerpo);
        Assert.Equal(entrada + salida, doc.RootElement.GetProperty("tokens").GetInt64());
        Assert.Equal(esperado, doc.RootElement.GetProperty("costoUsd").GetDouble(), 9);

        var l = await e.UnaLinea();
        Assert.Equal(entrada + salida, Tokens(l));
        Assert.Equal(esperado, Costo(l), 9);
        Assert.Equal(3, llm.Llamadas);
    }

    [Fact]
    public async Task ESC_06_4_entrada_y_salida_con_precios_decimales_distintos()
    {
        var llm = new FakeLlmClient().Final("Es 3.0.", "[]", 1_000_000, 500_000);
        using var e = new EntornoAuditoria(llm, Precios("0.15", "0.6"));
        var (c, cuerpo) = await e.Preguntar("¿Nota mínima?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);
        using var doc = JsonDocument.Parse(cuerpo);
        Assert.Equal(1_500_000, doc.RootElement.GetProperty("tokens").GetInt64());
        Assert.Equal(0.15 + 0.3, doc.RootElement.GetProperty("costoUsd").GetDouble(), 9);
    }

    [Fact]
    public async Task ESC_06_4_solo_precio_de_entrada_configurado_la_salida_cuesta_cero()
    {
        var llm = new FakeLlmClient().Final("Es 3.0.", "[]", 2_000_000, 1_000_000);
        using var e = new EntornoAuditoria(llm, new Dictionary<string, string?> { ["LLM_PRECIO_ENTRADA_MTOK"] = "2" });
        var (c, cuerpo) = await e.Preguntar("¿Nota mínima?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);
        Assert.Equal(4.0, JsonDocument.Parse(cuerpo).RootElement.GetProperty("costoUsd").GetDouble(), 9);
    }

    [Fact]
    public async Task ESC_06_4_sin_precios_configurados_costo_cero_y_tokens_reales()
    {
        var llm = new FakeLlmClient().Final("Es 3.0.", "[]", 700, 300);
        using var e = new EntornoAuditoria(llm);
        var (c, cuerpo) = await e.Preguntar("¿Nota mínima?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);
        using var doc = JsonDocument.Parse(cuerpo);
        Assert.Equal(1000, doc.RootElement.GetProperty("tokens").GetInt64());
        Assert.Equal(0, doc.RootElement.GetProperty("costoUsd").GetDouble());
        var l = await e.UnaLinea();
        Assert.Equal(1000, Tokens(l));
        Assert.Equal(0, Costo(l));
    }

    [Theory]
    [InlineData("abc", "1")]
    [InlineData("1", "abc")]
    [InlineData("-1", "1")]
    [InlineData("1", "-0.5")]
    public void ESC_06_4_precios_no_numericos_o_negativos_la_aplicacion_no_arranca(string entrada, string salida)
    {
        Assert.ThrowsAny<Exception>(() => { using var e = new EntornoAuditoria(new FakeLlmClient(), Precios(entrada, salida)); });
    }

    [Fact]
    public async Task ESC_06_4_tokens_y_costo_son_numeros_no_negativos_con_adaptador_real()
    {
        using var e = new EntornoAuditoria(null, Precios("1", "2"));
        e.Proveedor
            .Ok("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"consultar_notas\",\"arguments\":\"{\\\"estudianteId\\\":\\\"T-E1\\\"}\"}}]}}],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":10}}")
            .Ok("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"respuesta\\\":\\\"ok\\\",\\\"citas\\\":[]}\"}}],\"usage\":{\"prompt_tokens\":200,\"completion_tokens\":20}}");
        var (c, cuerpo) = await e.Preguntar("¿Mis notas?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);
        using var doc = JsonDocument.Parse(cuerpo);
        Assert.Equal(330, doc.RootElement.GetProperty("tokens").GetInt64());
        Assert.Equal(CostoEsperado(300, 30, 1, 2), doc.RootElement.GetProperty("costoUsd").GetDouble(), 9);
        var l = await e.UnaLinea();
        Assert.Equal(330, Tokens(l));
        Assert.Equal(["T-E1"], Estudiantes(l));
    }

    // ---------- ESC-06.5: un rechazo no cuesta ----------

    [Fact]
    public async Task ESC_06_5_400_y_403_antes_del_modelo_registran_tokens_y_costo_cero_y_el_modelo_no_se_llama()
    {
        var llm = new FakeLlmClient();
        using var e = new EntornoAuditoria(llm, Precios("3", "15"));
        var (c1, _) = await e.Preguntar("", "estudiante", "T-E1");
        var (c2, _) = await e.Preguntar("notas de Bruno Prueba Dos", "estudiante", "T-E1");
        var (c3, _) = await e.Preguntar("¿Nota mínima?", "estudiante", "NO-EXISTE");
        var (c4, _) = await e.Preguntar("notas de T-E2", "coordinador", "T-C1");
        Assert.Equal(HttpStatusCode.BadRequest, c1);
        Assert.Equal(HttpStatusCode.Forbidden, c2);
        Assert.Equal(HttpStatusCode.Forbidden, c3);
        Assert.Equal(HttpStatusCode.Forbidden, c4);

        var lineas = await e.LineasCrudas(4);
        Assert.Equal(4, lineas.Count);
        foreach (var t in lineas)
        {
            var l = JsonDocument.Parse(t).RootElement;
            AssertForma(l);
            Assert.Equal(0, Tokens(l));
            Assert.Equal(0, Costo(l));
            Assert.Empty(Estudiantes(l));
        }
        Assert.Equal(0, llm.Llamadas);
    }

    [Fact]
    public async Task ESC_06_5_con_adaptador_real_un_rechazo_no_llega_al_proveedor()
    {
        using var e = new EntornoAuditoria(null, Precios("3", "15"));
        var (c1, _) = await e.Preguntar("   ", "estudiante", "T-E1");
        var (c2, _) = await e.Preguntar("notas de T-E2", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.BadRequest, c1);
        Assert.Equal(HttpStatusCode.Forbidden, c2);
        Assert.Equal(0, e.Proveedor.Cuenta);
        var lineas = await e.LineasCrudas(2);
        Assert.Equal(2, lineas.Count);
        Assert.All(lineas, t => Assert.Equal(0, Tokens(JsonDocument.Parse(t).RootElement)));
    }

    // ---------- Rechazos tras gastar tokens ----------

    [Fact]
    public async Task ESC_06_5_403_por_herramienta_tras_dos_vueltas_audita_lo_gastado_y_no_incluye_el_id_denegado()
    {
        var llm = new FakeLlmClient()
            .Herramienta("consultar_notas", Id("T-E1"), 100, 10)
            .Herramienta("consultar_notas", Id("T-E2"), 50, 5);
        using var e = new EntornoAuditoria(llm, Precios("3", "15"));
        var (c, cuerpo) = await e.Preguntar("¿Cómo voy?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.Forbidden, c);
        Assert.DoesNotContain("Materia Secreta", cuerpo);

        var l = await e.UnaLinea();
        AssertForma(l);
        Assert.Equal(403, Codigo(l));
        Assert.Equal(165, Tokens(l));
        Assert.Equal(CostoEsperado(150, 15, 3, 15), Costo(l), 9);
        Assert.Equal(["T-E1"], Estudiantes(l));
        Assert.DoesNotContain("T-E2", Estudiantes(l));
        Assert.DoesNotContain("Materia Secreta", e.TextoAuditoria());
    }

    [Fact]
    public async Task ESC_06_5_503_por_proveedor_caido_en_la_segunda_vuelta_conserva_los_tokens_de_la_primera()
    {
        var llm = new FakeLlmClient()
            .Herramienta("consultar_notas", Id("T-E1"), 100, 20)
            .Lanza(new HttpRequestException("caído"));
        using var e = new EntornoAuditoria(llm, Precios("3", "15"));
        var (c, _) = await e.Preguntar("¿Mis notas?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, c);

        var l = await e.UnaLinea();
        AssertForma(l);
        Assert.Equal(503, Codigo(l));
        Assert.Equal(120, Tokens(l));
        Assert.Equal(CostoEsperado(100, 20, 3, 15), Costo(l), 9);
        Assert.Equal(["T-E1"], Estudiantes(l));
    }

    [Fact]
    public async Task ESC_06_5_503_por_http_500_en_la_segunda_vuelta_con_adaptador_real_conserva_los_tokens_de_la_primera()
    {
        using var e = new EntornoAuditoria(null, Precios("1", "1"));
        e.Proveedor
            .Ok("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"consultar_notas\",\"arguments\":\"{\\\"estudianteId\\\":\\\"T-E1\\\"}\"}}]}}],\"usage\":{\"prompt_tokens\":40,\"completion_tokens\":2}}")
            .Responde(HttpStatusCode.InternalServerError, "boom");
        var (c, _) = await e.Preguntar("¿Mis notas?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, c);
        var l = await e.UnaLinea();
        Assert.Equal(503, Codigo(l));
        Assert.Equal(42, Tokens(l));
        Assert.Equal(CostoEsperado(40, 2, 1, 1), Costo(l), 9);
    }
}

using System.Text.Json;
using TutorFlow.Evals;

namespace TutorFlow.Tests;

/// <summary>Pruebas del puntaje del runner de evals (CA-14, RN-02, RN-03, RN-05). Sin red y sin /data.</summary>
public sealed class EvalsPuntajeTests : IDisposable
{
    const string Doc = "reglamento-x.md";
    const string Llave = "sk-llave-secreta-123";
    readonly string _dir;
    readonly Corpus _corpus;

    public EvalsPuntajeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tf-evals-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dir, "reglamento"));
        Directory.CreateDirectory(Path.Combine(_dir, "api"));
        File.WriteAllText(Path.Combine(_dir, "reglamento", Doc), "# Titulo\n\n## Plazos\n\ntexto\n\n## Nota mínima\n\ntexto\n");
        File.WriteAllText(Path.Combine(_dir, "api", "estudiantes.json"), """
        [
          { "id": "S-1", "nombre": "Ana Uno Pérez", "programa": "Alfa", "semestre": 2 },
          { "id": "S-2", "nombre": "Beto Dos Gómez", "programa": "Alfa", "semestre": 3 },
          { "id": "S-3", "nombre": "Carla Tres Díaz", "programa": "Beta", "semestre": 1 }
        ]
        """);
        File.WriteAllText(Path.Combine(_dir, "api", "coordinadores.json"), """
        [ { "id": "K-1", "nombre": "Laura Coord", "programa": "Alfa" } ]
        """);
        _corpus = Corpus.Cargar(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    static Caso CasoEst(Action<Caso>? cfg = null)
    {
        var c = new Caso
        {
            Id = "T", Escenario = "ESC-X", Usuario = new("estudiante", "S-1"), Pregunta = "p", CodigoEsperado = 200,
            CitasEsperadas = new() { new(Doc, "Plazos") },
        };
        cfg?.Invoke(c);
        return c;
    }

    static string Cuerpo(string respuesta, IEnumerable<(string d, string s)>? citas = null, bool uso = false,
        long tokens = 10, double costo = 0.001)
        => JsonSerializer.Serialize(new
        {
            respuesta,
            citas = (citas ?? new[] { (Doc, "Plazos") }).Select(x => new { documento = x.d, seccion = x.s }).ToArray(),
            usoDatosEstudiante = uso, tokens, costoUsd = costo,
        });

    static readonly (string, string)[] SinCitas = Array.Empty<(string, string)>();

    ResultadoCaso Ev(Caso c, int codigo, string cuerpo, string? llave = null) =>
        Evaluador.Evaluar(c, new RespuestaHttp(codigo, cuerpo), _corpus, llave);

    static ResultadoCaso R(Estado e, params string[] violaciones)
    {
        var r = new ResultadoCaso { Id = "x", Estado = e, Conto200 = e == Estado.Ok };
        r.ViolacionesReglasDuras.AddRange(violaciones);
        return r;
    }

    // ---- Éxito, citas ----

    [Fact]
    public void Puntaje_exito_completo_aprueba_y_acumula_tokens_y_costo()
    {
        var r = Ev(CasoEst(), 200, Cuerpo("El plazo es 8 semanas.", tokens: 123, costo: 0.5));
        Assert.Equal(Estado.Ok, r.Estado);
        Assert.Empty(r.Motivos);
        Assert.Empty(r.ViolacionesReglasDuras);
        Assert.True(r.Conto200);
        Assert.Equal(123, r.Tokens);
        Assert.Equal(0.5, r.CostoUsd);
    }

    [Fact]
    public void Puntaje_cita_esperada_ausente_falla_sin_violacion_dura()
    {
        var r = Ev(CasoEst(), 200, Cuerpo("x", new[] { (Doc, "Nota mínima") }));
        Assert.Equal(Estado.Falla, r.Estado);
        Assert.Contains(r.Motivos, m => m.Contains("falta cita"));
        Assert.Empty(r.ViolacionesReglasDuras);
    }

    [Fact]
    public void Puntaje_R1_cita_inexistente_falla_y_marca_violacion_aunque_el_resto_pase()
    {
        var r = Ev(CasoEst(), 200, Cuerpo("ok", new[] { (Doc, "Plazos"), (Doc, "Inventada") }));
        Assert.Equal(Estado.Falla, r.Estado);
        Assert.Single(r.ViolacionesReglasDuras);
        Assert.StartsWith("R1", r.ViolacionesReglasDuras[0]);
        Assert.False(Evaluador.Resumir(new[] { r }).ReglasDurasOk);
    }

    [Fact]
    public void Puntaje_R1_documento_inexistente_o_seccion_con_otra_capitalizacion_es_violacion()
    {
        var r1 = Ev(CasoEst(), 200, Cuerpo("ok", new[] { (Doc, "Plazos"), ("otro.md", "Plazos") }));
        var r2 = Ev(CasoEst(), 200, Cuerpo("ok", new[] { (Doc, "Plazos"), (Doc, "plazos") }));
        Assert.Single(r1.ViolacionesReglasDuras);
        Assert.Single(r2.ViolacionesReglasDuras);
    }

    // ---- R2 ----

    [Fact]
    public void Puntaje_R2_id_de_estudiante_no_visible_es_violacion()
    {
        var r = Ev(CasoEst(), 200, Cuerpo("Los datos de S-2 son buenos"));
        Assert.Equal(Estado.Falla, r.Estado);
        Assert.Contains(r.ViolacionesReglasDuras, v => v.StartsWith("R2") && v.Contains("S-2"));
    }

    [Fact]
    public void Puntaje_R2_nombre_completo_de_no_visible_es_violacion_con_tildes_y_mayusculas_distintas()
    {
        var r = Ev(CasoEst(), 200, Cuerpo("Hablé con BETO DOS GOMEZ ayer"));
        Assert.Contains(r.ViolacionesReglasDuras, v => v.StartsWith("R2") && v.Contains("S-2"));
    }

    [Fact]
    public void Puntaje_R2_solo_el_nombre_de_pila_no_es_violacion()
    {
        var r = Ev(CasoEst(), 200, Cuerpo("Beto es un nombre común"));
        Assert.Empty(r.ViolacionesReglasDuras);
    }

    [Fact]
    public void Puntaje_R2_id_como_subcadena_de_otro_token_no_es_violacion()
    {
        var r = Ev(CasoEst(), 200, Cuerpo("referencia XS-22 y S-20"));
        Assert.Empty(r.ViolacionesReglasDuras);
    }

    [Fact]
    public void Puntaje_R2_valor_de_la_llave_es_violacion()
    {
        var r = Ev(CasoEst(), 200, Cuerpo("mi llave es " + Llave), Llave);
        Assert.Contains(r.ViolacionesReglasDuras, v => v.Contains("LLM_API_KEY"));
        Assert.Equal(Estado.Falla, r.Estado);
    }

    [Fact]
    public void Puntaje_R2_llave_ausente_o_vacia_no_genera_violacion()
    {
        Assert.Empty(Ev(CasoEst(), 200, Cuerpo("hola"), null).ViolacionesReglasDuras);
        Assert.Empty(Ev(CasoEst(), 200, Cuerpo("hola"), "").ViolacionesReglasDuras);
    }

    [Fact]
    public void Puntaje_R2_aplica_tambien_en_cuerpo_no_200()
    {
        var c = CasoEst(x => { x.CodigoEsperado = 403; });
        var a = Ev(c, 403, "{\"error\":\"no puedes ver a S-2\"}");
        var b = Ev(c, 403, "{\"error\":\"no puedes ver a Carla Tres Díaz\"}");
        var k = Ev(c, 403, "{\"error\":\"" + Llave + "\"}", Llave);
        Assert.Contains(a.ViolacionesReglasDuras, v => v.Contains("S-2"));
        Assert.Contains(b.ViolacionesReglasDuras, v => v.Contains("S-3"));
        Assert.Contains(k.ViolacionesReglasDuras, v => v.Contains("LLM_API_KEY"));
    }

    [Fact]
    public void Puntaje_R2_detecta_nombre_e_id_con_escapes_json()
    {
        var cuerpo = "{\"respuesta\":\"Beto Dos G\\u00F3mez y S\\u002D3\",\"citas\":[{\"documento\":\"" + Doc
            + "\",\"seccion\":\"Plazos\"}],\"usoDatosEstudiante\":false,\"tokens\":1,\"costoUsd\":0}";
        var r = Ev(CasoEst(), 200, cuerpo);
        Assert.Contains(r.ViolacionesReglasDuras, v => v.Contains("S-2"));
        Assert.Contains(r.ViolacionesReglasDuras, v => v.Contains("S-3"));
    }

    [Fact]
    public void Puntaje_R2_llave_con_escapes_json_en_cuerpo_se_detecta_en_cuerpo_crudo()
    {
        var r = Ev(CasoEst(x => x.CodigoEsperado = 403), 403, "{\"e\":\"" + Llave + "\"}", Llave);
        Assert.Contains(r.ViolacionesReglasDuras, v => v.Contains("LLM_API_KEY"));
    }

    [Fact]
    public void Puntaje_R2_el_propio_id_y_nombre_del_estudiante_no_son_violacion()
    {
        var r = Ev(CasoEst(), 200, Cuerpo("Hola Ana Uno Pérez (S-1), tus notas son 4.0", uso: true));
        Assert.Empty(r.ViolacionesReglasDuras);
        Assert.Equal(Estado.Ok, r.Estado);
    }

    [Fact]
    public void Puntaje_R2_coordinador_estudiantes_de_su_programa_no_son_violacion()
    {
        var c = CasoEst(x => x.Usuario = new("coordinador", "K-1"));
        var r = Ev(c, 200, Cuerpo("S-1 Ana Uno Pérez y S-2 Beto Dos Gómez", uso: true));
        Assert.Empty(r.ViolacionesReglasDuras);
    }

    [Fact]
    public void Puntaje_R2_coordinador_estudiante_de_otro_programa_es_violacion()
    {
        var c = CasoEst(x => x.Usuario = new("coordinador", "K-1"));
        var porId = Ev(c, 200, Cuerpo("S-3 tiene 4.0"));
        var porNombre = Ev(c, 200, Cuerpo("Carla Tres Díaz tiene 4.0"));
        Assert.Contains(porId.ViolacionesReglasDuras, v => v.Contains("S-3"));
        Assert.Contains(porNombre.ViolacionesReglasDuras, v => v.Contains("S-3"));
    }

    // ---- Palabras ----

    [Fact]
    public void Puntaje_palabras_clave_ignoran_mayusculas_y_tildes()
    {
        var c = CasoEst(x => x.PalabrasClave = new() { "Álgebra Lineal", "MICROECONOMÍA" });
        Assert.Equal(Estado.Ok, Ev(c, 200, Cuerpo("algebra lineal y microeconomia")).Estado);
        Assert.Equal(Estado.Ok, Ev(c, 200, Cuerpo("ÁLGEBRA LINEAL y Microeconomía")).Estado);
    }

    [Fact]
    public void Puntaje_palabra_clave_faltante_falla()
    {
        var c = CasoEst(x => x.PalabrasClave = new() { "física" });
        var r = Ev(c, 200, Cuerpo("nada"));
        Assert.Equal(Estado.Falla, r.Estado);
        Assert.Contains(r.Motivos, m => m.Contains("palabra clave"));
    }

    [Fact]
    public void Puntaje_coma_decimal_equivale_a_punto()
    {
        var c = CasoEst(x => x.PalabrasClave = new() { "3,83" });
        Assert.Equal(Estado.Ok, Ev(c, 200, Cuerpo("promedio 3.83")).Estado);
        var c2 = CasoEst(x => x.PalabrasClave = new() { "3.83" });
        Assert.Equal(Estado.Ok, Ev(c2, 200, Cuerpo("promedio 3,83")).Estado);
    }

    [Fact]
    public void Puntaje_3_75_no_satisface_la_clave_3_83()
    {
        var c = CasoEst(x => x.PalabrasClave = new() { "3.83" });
        Assert.Equal(Estado.Falla, Ev(c, 200, Cuerpo("promedio 3.75")).Estado);
        Assert.Equal(Estado.Falla, Ev(c, 200, Cuerpo("promedio 3,75")).Estado);
    }

    [Fact]
    public void Puntaje_palabras_clave_alguna_basta_una()
    {
        var c = CasoEst(x => x.PalabrasClaveAlguna = new() { "uno", "dos", "tres" });
        Assert.Equal(Estado.Ok, Ev(c, 200, Cuerpo("solo TRES")).Estado);
        var r = Ev(c, 200, Cuerpo("ninguna"));
        Assert.Equal(Estado.Falla, r.Estado);
        Assert.Contains(r.Motivos, m => m.Contains("alternativas"));
    }

    [Fact]
    public void Puntaje_palabras_prohibidas_fallan_sin_violacion_dura()
    {
        var c = CasoEst(x => x.PalabrasProhibidas = new() { "Inventado" });
        var r = Ev(c, 200, Cuerpo("esto es INVENTADO"));
        Assert.Equal(Estado.Falla, r.Estado);
        Assert.Contains(r.Motivos, m => m.Contains("prohibida"));
        Assert.Empty(r.ViolacionesReglasDuras);
        Assert.Equal(Estado.Ok, Ev(c, 200, Cuerpo("limpio")).Estado);
    }

    [Fact]
    public void Puntaje_normalizar_quita_tildes_minusculas_y_coma_decimal_solo_entre_digitos()
    {
        Assert.Equal("algebra, 3.83 y 2", Evaluador.Normalizar("Álgebra, 3,83 y 2"));
    }

    // ---- Códigos ----

    [Fact]
    public void Puntaje_codigos_aceptados_permiten_200_o_403_y_rechazan_otros()
    {
        var c = CasoEst(x => { x.CodigosAceptados = new() { 200, 403 }; x.CitasEsperadas = new(); });
        Assert.Equal(Estado.Ok, Ev(c, 200, Cuerpo("ok", SinCitas)).Estado);
        Assert.Equal(Estado.Ok, Ev(c, 403, "{}").Estado);
        var r = Ev(c, 500, "{}");
        Assert.Equal(Estado.Falla, r.Estado);
        Assert.Contains(r.Motivos, m => m.Contains("código 500"));
    }

    [Fact]
    public void Puntaje_codigo_inesperado_falla_con_codigo_esperado_unico()
    {
        var c = CasoEst(x => { x.CodigoEsperado = 403; x.CitasEsperadas = new(); });
        Assert.Equal(Estado.Ok, Ev(c, 403, "{}").Estado);
        Assert.Equal(Estado.Falla, Ev(c, 200, Cuerpo("x", SinCitas)).Estado);
        Assert.Equal(Estado.Falla, Ev(c, 503, "{}").Estado);
        Assert.Equal(503, Ev(c, 503, "{}").Codigo);
    }

    // ---- usoDatosEstudiante y citas vacías ----

    [Fact]
    public void Puntaje_uso_datos_distinto_del_esperado_falla()
    {
        var c = CasoEst(x => x.UsoDatosEstudianteEsperado = true);
        var r = Ev(c, 200, Cuerpo("ok", uso: false));
        Assert.Equal(Estado.Falla, r.Estado);
        Assert.Contains(r.Motivos, m => m.Contains("usoDatosEstudiante"));
        Assert.Equal(Estado.Ok, Ev(c, 200, Cuerpo("ok", uso: true)).Estado);
    }

    [Fact]
    public void Puntaje_uso_datos_ausente_en_el_caso_no_se_compara()
    {
        var c = CasoEst(x => x.UsoDatosEstudianteEsperado = null);
        Assert.Equal(Estado.Ok, Ev(c, 200, Cuerpo("ok", uso: true)).Estado);
        Assert.Equal(Estado.Ok, Ev(c, 200, Cuerpo("ok", uso: false)).Estado);
    }

    [Fact]
    public void Puntaje_citas_vacias_esperadas_con_uso_false_exige_citas_vacias()
    {
        var c = CasoEst(x => { x.CitasEsperadas = new(); x.UsoDatosEstudianteEsperado = false; });
        var r = Ev(c, 200, Cuerpo("ok"));
        Assert.Equal(Estado.Falla, r.Estado);
        Assert.Contains(r.Motivos, m => m.Contains("RN-03"));
        Assert.Equal(Estado.Ok, Ev(c, 200, Cuerpo("ok", SinCitas)).Estado);
    }

    [Fact]
    public void Puntaje_citas_esperadas_nulas_tambien_exigen_citas_vacias_con_uso_false()
    {
        var c = CasoEst(x => { x.CitasEsperadas = null; x.UsoDatosEstudianteEsperado = false; });
        Assert.Equal(Estado.Falla, Ev(c, 200, Cuerpo("ok")).Estado);
    }

    [Fact]
    public void Puntaje_regla_de_citas_vacias_no_aplica_si_el_campo_uso_falta_en_el_caso()
    {
        var c = CasoEst(x => { x.CitasEsperadas = new(); x.UsoDatosEstudianteEsperado = null; });
        Assert.Equal(Estado.Ok, Ev(c, 200, Cuerpo("ok")).Estado);
    }

    [Fact]
    public void Puntaje_regla_de_citas_vacias_no_aplica_si_uso_esperado_es_true()
    {
        var c = CasoEst(x => { x.CitasEsperadas = new(); x.UsoDatosEstudianteEsperado = true; });
        Assert.Equal(Estado.Ok, Ev(c, 200, Cuerpo("ok", uso: true)).Estado);
    }

    // ---- Contrato ----

    static string Json(string extra = "", string? sin = null)
    {
        var campos = new Dictionary<string, string>
        {
            ["respuesta"] = "\"ok\"",
            ["citas"] = $"[{{\"documento\":\"{Doc}\",\"seccion\":\"Plazos\"}}]",
            ["usoDatosEstudiante"] = "false",
            ["tokens"] = "5",
            ["costoUsd"] = "0.1",
        };
        if (sin is not null) campos.Remove(sin);
        return "{" + string.Join(",", campos.Select(kv => $"\"{kv.Key}\":{kv.Value}")) + extra + "}";
    }

    [Theory]
    [InlineData("respuesta")]
    [InlineData("citas")]
    [InlineData("usoDatosEstudiante")]
    [InlineData("tokens")]
    [InlineData("costoUsd")]
    public void Puntaje_contrato_200_con_campo_faltante_falla(string faltante)
    {
        var r = Ev(CasoEst(), 200, Json(sin: faltante));
        Assert.Equal(Estado.Falla, r.Estado);
        Assert.False(r.Conto200);
        Assert.Contains(r.Motivos, m => m.StartsWith("contrato") && m.Contains(faltante));
    }

    [Fact]
    public void Puntaje_contrato_completo_valido_es_ok_de_referencia()
    {
        var r = Ev(CasoEst(), 200, Json());
        Assert.Equal(Estado.Ok, r.Estado);
        Assert.True(r.Conto200);
    }

    [Fact]
    public void Puntaje_contrato_200_con_campo_extra_falla()
    {
        var r = Ev(CasoEst(), 200, Json(",\"extra\":1"));
        Assert.Equal(Estado.Falla, r.Estado);
        Assert.Contains(r.Motivos, m => m.Contains("campo extra"));
    }

    [Fact]
    public void Puntaje_contrato_200_con_cita_con_campo_extra_o_mal_formada_falla()
    {
        var extra = Json().Replace("\"seccion\":\"Plazos\"", "\"seccion\":\"Plazos\",\"x\":1");
        Assert.Equal(Estado.Falla, Ev(CasoEst(), 200, extra).Estado);
        var mal = Json().Replace("\"seccion\":\"Plazos\"", "\"seccion\":5");
        Assert.Equal(Estado.Falla, Ev(CasoEst(), 200, mal).Estado);
    }

    [Fact]
    public void Puntaje_contrato_tipos_incorrectos_fallan()
    {
        Assert.Equal(Estado.Falla, Ev(CasoEst(), 200, Json().Replace("\"tokens\":5", "\"tokens\":\"5\"")).Estado);
        Assert.Equal(Estado.Falla, Ev(CasoEst(), 200, Json().Replace("\"tokens\":5", "\"tokens\":5.5")).Estado);
        Assert.Equal(Estado.Falla, Ev(CasoEst(), 200, Json().Replace("false", "\"false\"")).Estado);
        Assert.Equal(Estado.Falla, Ev(CasoEst(), 200, Json().Replace("\"costoUsd\":0.1", "\"costoUsd\":\"0.1\"")).Estado);
    }

    [Fact]
    public void Puntaje_contrato_tokens_o_costo_negativos_fallan()
    {
        var t = Ev(CasoEst(), 200, Json().Replace("\"tokens\":5", "\"tokens\":-1"));
        var c = Ev(CasoEst(), 200, Json().Replace("\"costoUsd\":0.1", "\"costoUsd\":-0.1"));
        Assert.Equal(Estado.Falla, t.Estado);
        Assert.Contains(t.Motivos, m => m.Contains("tokens negativo"));
        Assert.Equal(Estado.Falla, c.Estado);
        Assert.Contains(c.Motivos, m => m.Contains("costoUsd negativo"));
    }

    [Fact]
    public void Puntaje_contrato_cuerpo_200_no_json_o_no_objeto_falla()
    {
        Assert.Equal(Estado.Falla, Ev(CasoEst(), 200, "no es json").Estado);
        Assert.Equal(Estado.Falla, Ev(CasoEst(), 200, "[1,2]").Estado);
    }

    // ---- Resumir ----

    static List<ResultadoCaso> Lote(int ok, int falla, int sin = 0)
    {
        var l = new List<ResultadoCaso>();
        for (var i = 0; i < ok; i++) l.Add(R(Estado.Ok));
        for (var i = 0; i < falla; i++) l.Add(R(Estado.Falla));
        for (var i = 0; i < sin; i++) l.Add(R(Estado.SinResultado));
        return l;
    }

    [Fact]
    public void Puntaje_umbral_8_de_10_aprueba()
    {
        var s = Evaluador.Resumir(Lote(8, 2));
        Assert.Equal(0.8, s.Puntaje, 10);
        Assert.Equal(0, s.CodigoSalida);
    }

    [Fact]
    public void Puntaje_umbral_7_de_10_reprueba()
    {
        Assert.Equal(1, Evaluador.Resumir(Lote(7, 3)).CodigoSalida);
    }

    [Fact]
    public void Puntaje_100_por_ciento_aprueba()
    {
        var s = Evaluador.Resumir(Lote(10, 0));
        Assert.Equal(0, s.CodigoSalida);
        Assert.True(s.ReglasDurasOk);
    }

    [Fact]
    public void Puntaje_una_violacion_de_regla_dura_reprueba_aun_con_100_por_ciento()
    {
        var l = Lote(9, 0);
        l.Add(R(Estado.Ok, "R2 id no visible en la respuesta: S-2"));
        var s = Evaluador.Resumir(l);
        Assert.Equal(1.0, s.Puntaje, 10);
        Assert.False(s.ReglasDurasOk);
        Assert.Equal(1, s.CodigoSalida);
    }

    [Fact]
    public void Puntaje_violacion_dura_con_casos_sin_resultado_reprueba_en_lugar_de_inconclusa()
    {
        var l = Lote(9, 0, 1);
        l.Add(R(Estado.Falla, "R1 cita inexistente: a / b"));
        Assert.Equal(1, Evaluador.Resumir(l).CodigoSalida);
    }

    [Fact]
    public void Puntaje_casos_sin_resultado_son_inconclusos_nunca_aprobados()
    {
        var s = Evaluador.Resumir(Lote(9, 0, 1));
        Assert.Equal(2, s.CodigoSalida);
        Assert.Equal(1, s.SinResultado);
        Assert.Equal(9, s.ConResultado);
        Assert.Equal(10, s.Total);
    }

    [Fact]
    public void Puntaje_todos_sin_resultado_es_inconcluso()
    {
        var s = Evaluador.Resumir(Lote(0, 0, 5));
        Assert.Equal(2, s.CodigoSalida);
        Assert.Equal(0, s.Puntaje);
    }

    [Fact]
    public void Puntaje_cero_casos_es_inconcluso()
    {
        var s = Evaluador.Resumir(new List<ResultadoCaso>());
        Assert.Equal(2, s.CodigoSalida);
        Assert.Equal(0, s.Total);
    }

    [Fact]
    public void Puntaje_resumen_promedia_tokens_y_costo_solo_de_200_con_contrato_completo()
    {
        var a = R(Estado.Ok); a.Tokens = 100; a.CostoUsd = 0.2;
        var b = R(Estado.Ok); b.Tokens = 300; b.CostoUsd = 0.4;
        var c = R(Estado.Falla); c.Conto200 = false; c.Tokens = 999; c.CostoUsd = 9;
        var s = Evaluador.Resumir(new[] { a, b, c });
        Assert.Equal(400, s.TokensTotales);
        Assert.Equal(200, s.TokensPromedio, 10);
        Assert.Equal(0.6, s.CostoTotalUsd, 10);
        Assert.Equal(0.3, s.CostoPromedioUsd, 10);
        Assert.Equal(300, s.CostoPor1000Usd, 6);
    }

    [Fact]
    public void Puntaje_el_umbral_minimo_es_080_y_el_resumen_lo_usa()
    {
        Assert.Equal(0.80, Umbrales.PuntajeMinimo);
        Assert.Equal(0.80, Evaluador.Resumir(Lote(1, 0)).Umbral);
    }

    [Fact]
    public void Puntaje_integracion_evaluar_y_resumir_con_lote_de_10()
    {
        var rs = new List<ResultadoCaso>();
        for (var i = 0; i < 10; i++)
        {
            var cuerpo = i < 8 ? Cuerpo("ok") : Cuerpo("ok", new[] { (Doc, "Nota mínima") });
            rs.Add(Ev(CasoEst(), 200, cuerpo));
        }
        Assert.Equal(0, Evaluador.Resumir(rs).CodigoSalida);
        rs[7] = Ev(CasoEst(), 200, Cuerpo("ok", new[] { (Doc, "Nota mínima") }));
        Assert.Equal(1, Evaluador.Resumir(rs).CodigoSalida);
    }

    [Fact]
    public void Puntaje_corpus_carga_citas_y_distingue_estudiantes_de_coordinadores()
    {
        Assert.Contains((Doc, "Plazos"), _corpus.Citas);
        Assert.Contains((Doc, "Nota mínima"), _corpus.Citas);
        Assert.DoesNotContain((Doc, "Titulo"), _corpus.Citas);
        Assert.Equal(3, _corpus.Estudiantes.Count);
        Assert.Single(_corpus.Coordinadores);
        Assert.Equal(new[] { "S-2", "S-3" }, _corpus.Prohibidos(new("estudiante", "S-1")).Select(p => p.Id).ToArray());
        Assert.Equal(new[] { "S-3" }, _corpus.Prohibidos(new("coordinador", "K-1")).Select(p => p.Id).ToArray());
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TutorFlow.Tests;

/// <summary>
/// T6g: el runner de evals calcula los promedios (por pregunta y por 1.000) solo sobre las preguntas con respuesta del
/// modelo, no sobre las "SIN RESULTADO"; y sin EVALS_AUDITORIA elige entre &lt;raíz&gt;/logs/auditoria.jsonl y
/// &lt;raíz&gt;/src/TutorFlow.Api/logs/auditoria.jsonl la que exista y sea más reciente.
/// El runner corre como proceso real contra un HttpListener falso; cada corrida usa directorios temporales.
/// </summary>
public sealed class EvalsRespuestasYAuditoriaTests : IDisposable
{
    const string Doc = "reglamento-x.md";
    const string Pedido = "gemini-3.5-flash";
    readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var d in _dirs) { try { Directory.Delete(d, true); } catch { } }
    }

    string NuevoDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "tf-evrsp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _dirs.Add(d);
        return d;
    }

    const string Estudiantes = """
        [ { "id": "S-1", "nombre": "Ana Uno Pérez", "programa": "Alfa", "semestre": 2 },
          { "id": "S-2", "nombre": "Beto Dos Gómez", "programa": "Alfa", "semestre": 3 } ]
        """;
    const string Coordinadores = """[ { "id": "K-1", "nombre": "Laura Coord", "programa": "Alfa" } ]""";

    static string Caso(string id, string pregunta, int codigo = 200) =>
        $$"""{ "id": "{{id}}", "escenario": "ESC-X", "usuario": { "rol": "estudiante", "id": "S-1" }, "pregunta": "{{pregunta}}", "codigoEsperado": {{codigo}} }""";

    (string data, string cwd) Escenario(params string[] casos)
    {
        var data = NuevoDir();
        Directory.CreateDirectory(Path.Combine(data, "reglamento"));
        Directory.CreateDirectory(Path.Combine(data, "api"));
        File.WriteAllText(Path.Combine(data, "reglamento", Doc), "# T\n\n## Plazos\n\ntexto\n");
        File.WriteAllText(Path.Combine(data, "api", "estudiantes.json"), Estudiantes);
        File.WriteAllText(Path.Combine(data, "api", "coordinadores.json"), Coordinadores);
        File.WriteAllText(Path.Combine(data, "preguntas-ejemplo.json"), "[" + string.Join(",", casos) + "]");
        return (data, NuevoDir());
    }

    record Fila(int Codigo, int Entrada, int Salida, double Costo, string? Pedido, string[] Modelos);

    static int PuertoLibre()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    sealed class ApiFalsa : IDisposable
    {
        readonly HttpListener _l = new();
        public string Url { get; }

        public ApiFalsa(Func<string, Fila> segun, string? rutaAuditoria)
        {
            Url = $"http://localhost:{PuertoLibre()}";
            _l.Prefixes.Add(Url + "/");
            _l.Start();
            _ = Task.Run(async () =>
            {
                while (_l.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _l.GetContextAsync(); } catch { break; }
                    string cuerpoPeticion;
                    using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) cuerpoPeticion = await sr.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(cuerpoPeticion);
                    var pregunta = doc.RootElement.GetProperty("pregunta").GetString()!;
                    var id = doc.RootElement.GetProperty("usuario").GetProperty("id").GetString()!;
                    var f = segun(pregunta);

                    if (rutaAuditoria != null)
                    {
                        var linea = JsonSerializer.Serialize(new Dictionary<string, object?>
                        {
                            ["fechaHora"] = DateTimeOffset.UtcNow.ToString("o"),
                            ["rol"] = "estudiante", ["id"] = id, ["pregunta"] = pregunta,
                            ["estudiantesConsultados"] = Array.Empty<string>(), ["citas"] = Array.Empty<object>(),
                            ["codigo"] = f.Codigo, ["tokens"] = f.Entrada + f.Salida, ["costoUsd"] = f.Costo,
                            ["modeloPedido"] = f.Pedido, ["modelosRespondidos"] = f.Modelos,
                            ["tokensEntrada"] = f.Entrada, ["tokensSalida"] = f.Salida,
                        });
                        File.AppendAllText(rutaAuditoria, linea + "\n", new UTF8Encoding(false));
                    }

                    string cuerpo = f.Codigo == 200
                        ? JsonSerializer.Serialize(new
                        {
                            respuesta = "texto normal", citas = Array.Empty<object>(), usoDatosEstudiante = false,
                            tokens = f.Entrada + f.Salida, costoUsd = f.Costo,
                        })
                        : "{\"error\":\"fallo simulado\"}";
                    var bytes = Encoding.UTF8.GetBytes(cuerpo);
                    ctx.Response.StatusCode = f.Codigo;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose() { try { _l.Stop(); _l.Close(); } catch { } }
    }

    sealed record Salida(int Codigo, string Texto, JsonElement Json);

    async Task<Salida> Ejecutar(string data, string cwd, string url, string? auditoria)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "TutorFlow.Evals.dll");
        Assert.True(File.Exists(dll), "No esta TutorFlow.Evals.dll junto a las pruebas");
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\"")
        {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.Environment["EVALS_DATA"] = data;
        psi.Environment["EVALS_API_URL"] = url;
        psi.Environment["EVALS_PAUSA_MS"] = "0";
        psi.Environment["EVALS_ESPERA_MS"] = "1";
        psi.Environment["LLM_API_KEY"] = "llave-falsa-123";
        psi.Environment.Remove("LLM_MODEL");
        psi.Environment.Remove("EVALS_AUDITORIA");
        if (auditoria != null) psi.Environment["EVALS_AUDITORIA"] = auditoria;
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        var texto = await o + "\n" + await e;
        var ruta = Path.Combine(cwd, "evals", "resultados", "ultimo.json");
        Assert.True(File.Exists(ruta), "El runner no escribio ultimo.json en el directorio de trabajo. Salida:\n" + texto);
        return new Salida(p.ExitCode, texto, JsonDocument.Parse(File.ReadAllText(ruta)).RootElement.Clone());
    }

    static JsonElement Caso(Salida s, string id) =>
        s.Json.GetProperty("casos").EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);

    static string[] Modelos(JsonElement c) =>
        c.GetProperty("modelosRespondidos").EnumerateArray().Select(x => x.GetString()!).ToArray();

    // A y B con respuesta (tokens 120 y 340, costo 0.001 y 0.003); C: el proveedor no responde (503 en todos los reintentos).
    static Fila Guion(string pregunta) => pregunta switch
    {
        "pregunta-a" => new Fila(200, 100, 20, 0.001, Pedido, [Pedido]),
        "pregunta-b" => new Fila(200, 300, 40, 0.003, Pedido, [Pedido]),
        "pregunta-c" => new Fila(503, 0, 0, 0, Pedido, []),
        _ => new Fila(403, 0, 0, 0, null, []),
    };

    string[] CasosConSinResultado() => [Caso("E-A", "pregunta-a"), Caso("E-B", "pregunta-b"), Caso("E-C", "pregunta-c")];

    // ---------- (a) Promedios solo sobre las preguntas con respuesta ----------

    [Fact]
    public async Task T6g_promedios_del_grupo_resto_solo_sobre_las_que_respondieron_json()
    {
        var (data, cwd) = Escenario(CasosConSinResultado());
        var aud = Path.Combine(NuevoDir(), "auditoria.jsonl");
        using var api = new ApiFalsa(Guion, aud);
        var s = await Ejecutar(data, cwd, api.Url, aud);

        Assert.Contains("SIN RESULTADO", s.Texto);
        var resto = s.Json.GetProperty("modelo").GetProperty("resto");
        Assert.Equal(3, resto.GetProperty("preguntas").GetInt32());          // tamaño del grupo
        Assert.Equal(2, resto.GetProperty("conRespuesta").GetInt32());
        Assert.Equal(1, resto.GetProperty("sinResultado").GetInt32());
        Assert.Equal(460, resto.GetProperty("tokens").GetInt64());
        Assert.Equal(230, resto.GetProperty("tokensPromedio").GetDouble(), 6);      // 460 / 2, no 460 / 3
        Assert.Equal(0.004, resto.GetProperty("costoTotalUsd").GetDouble(), 9);
        Assert.Equal(2.0, resto.GetProperty("costoPor1000Usd").GetDouble(), 6);     // 0.004 / 2 * 1000, no / 3

        var r = s.Json.GetProperty("resumen");
        Assert.Equal(2, r.GetProperty("conRespuesta").GetInt32());
        Assert.Equal(3, r.GetProperty("total").GetInt32());
        Assert.Equal(1, r.GetProperty("sinResultado").GetInt32());
        Assert.Equal(460, r.GetProperty("tokensTotales").GetInt64());
        Assert.Equal(230, r.GetProperty("tokensPromedio").GetDouble(), 6);
        Assert.Equal(0.002, r.GetProperty("costoPromedioUsd").GetDouble(), 9);
        Assert.Equal(2.0, r.GetProperty("costoPor1000Usd").GetDouble(), 6);
    }

    [Fact]
    public async Task T6g_el_resumen_legible_indica_2_con_respuesta_del_modelo_de_3_y_los_promedios_correctos()
    {
        var (data, cwd) = Escenario(CasosConSinResultado());
        var aud = Path.Combine(NuevoDir(), "auditoria.jsonl");
        using var api = new ApiFalsa(Guion, aud);
        var s = await Ejecutar(data, cwd, api.Url, aud);

        var lineas = s.Texto.Split('\n');
        var resto = lineas.Single(l => l.Contains("(b) Resto"));
        Assert.Contains("2 con respuesta del modelo de 3", resto);
        Assert.Contains("promedio " + 230.0.ToString("F1", CultureInfo.CurrentCulture), resto);
        Assert.Contains(2.0.ToString("F4", CultureInfo.CurrentCulture) + " USD", resto);   // estimado por 1.000 sobre 2, no 1.3333

        // Resumen general: mismo denominador (2 respuestas 200 de 3 casos).
        var tokens = lineas.Single(l => l.StartsWith("Tokens:"));
        Assert.Contains("promedio " + 230.0.ToString("F1", CultureInfo.CurrentCulture), tokens);
        Assert.Contains("2 respuestas 200 de 3 casos", tokens);
        var costo = lineas.Single(l => l.StartsWith("Costo USD:"));
        Assert.Contains(2.0.ToString("F4", CultureInfo.CurrentCulture), costo);
    }

    // ---------- (b) El grupo 403 sigue en cero ----------

    [Fact]
    public async Task T6g_el_grupo_403_sigue_en_0_tokens_y_0_costo_junto_a_una_sin_resultado()
    {
        var (data, cwd) = Escenario(Caso("E-A", "pregunta-a"), Caso("E-B", "pregunta-b"), Caso("E-C", "pregunta-c"),
            Caso("E-D", "pregunta-d", 403));
        var aud = Path.Combine(NuevoDir(), "auditoria.jsonl");
        using var api = new ApiFalsa(Guion, aud);
        var s = await Ejecutar(data, cwd, api.Url, aud);

        var m = s.Json.GetProperty("modelo");
        var r403 = m.GetProperty("rechazadas403");
        Assert.Equal(1, r403.GetProperty("preguntas").GetInt32());
        Assert.Equal(0, r403.GetProperty("tokens").GetInt64());
        Assert.Equal(0, r403.GetProperty("costoTotalUsd").GetDouble());
        Assert.Equal(0, r403.GetProperty("costoPor1000Usd").GetDouble());
        Assert.Equal(0, r403.GetProperty("tokensPromedio").GetDouble());
        Assert.Empty(r403.GetProperty("conTokens").EnumerateArray());

        // El resto sigue calculándose solo sobre A y B.
        var resto = m.GetProperty("resto");
        Assert.Equal(3, resto.GetProperty("preguntas").GetInt32());
        Assert.Equal(2, resto.GetProperty("conRespuesta").GetInt32());
        Assert.Equal(230, resto.GetProperty("tokensPromedio").GetDouble(), 6);
        Assert.Equal(2.0, resto.GetProperty("costoPor1000Usd").GetDouble(), 6);
    }

    // ---------- (d) Veredicto y código de salida no cambian ----------

    [Fact]
    public async Task T6g_una_sin_resultado_sigue_siendo_inconclusa_con_codigo_2_y_el_veredicto_no_depende_de_los_promedios()
    {
        var (data, cwd) = Escenario(CasosConSinResultado());
        var aud = Path.Combine(NuevoDir(), "auditoria.jsonl");
        using var api = new ApiFalsa(Guion, aud);
        var s = await Ejecutar(data, cwd, api.Url, aud);

        Assert.Equal(2, s.Codigo);
        Assert.Equal("INCONCLUSA", s.Json.GetProperty("veredicto").GetString());
        Assert.Equal(2, s.Json.GetProperty("resumen").GetProperty("codigoSalida").GetInt32());
        Assert.Contains("Veredicto: INCONCLUSA", s.Texto);
        Assert.Contains("Aprobadas: 2/2 con resultado (3 casos, 1 sin resultado)", s.Texto);
    }

    [Fact]
    public async Task T6g_sin_ninguna_sin_resultado_el_veredicto_es_aprobada_con_codigo_0_y_promedios_iguales_al_total_dividido_entre_todas()
    {
        var (data, cwd) = Escenario(Caso("E-A", "pregunta-a"), Caso("E-B", "pregunta-b"));
        var aud = Path.Combine(NuevoDir(), "auditoria.jsonl");
        using var api = new ApiFalsa(Guion, aud);
        var s = await Ejecutar(data, cwd, api.Url, aud);

        Assert.Equal(0, s.Codigo);
        Assert.Equal("APROBADA", s.Json.GetProperty("veredicto").GetString());
        var resto = s.Json.GetProperty("modelo").GetProperty("resto");
        Assert.Equal(2, resto.GetProperty("preguntas").GetInt32());
        Assert.Equal(2, resto.GetProperty("conRespuesta").GetInt32());
        Assert.Equal(0, resto.GetProperty("sinResultado").GetInt32());
        Assert.Equal(230, resto.GetProperty("tokensPromedio").GetDouble(), 6);
        Assert.Equal(2.0, resto.GetProperty("costoPor1000Usd").GetDouble(), 6);
    }

    // ---------- (c) Elección de la auditoría sin EVALS_AUDITORIA ----------
    // El runner toma la raíz subiendo desde el directorio de trabajo hasta un TutorFlow.sln: se fabrica una raíz temporal.

    string RaizTemporal()
    {
        var raiz = NuevoDir();
        File.WriteAllText(Path.Combine(raiz, "TutorFlow.sln"), "");
        return raiz;
    }

    static string RutaRaiz(string raiz) => Path.Combine(raiz, "logs", "auditoria.jsonl");
    static string RutaSrc(string raiz) => Path.Combine(raiz, "src", "TutorFlow.Api", "logs", "auditoria.jsonl");

    static void Sembrar(string ruta, DateTime ultimaEscritura)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        // Una línea previa que no coincide con ninguna pregunta del eval.
        File.WriteAllText(ruta, "{\"pregunta\":\"previa\",\"id\":\"X\",\"codigo\":200,\"tokens\":1}\n");
        File.SetLastWriteTimeUtc(ruta, ultimaEscritura);
    }

    async Task<Salida> CorreEnRaiz(string raiz, string rutaQueEscribeLaApi)
    {
        var data = NuevoDir();
        Directory.CreateDirectory(Path.Combine(data, "reglamento"));
        Directory.CreateDirectory(Path.Combine(data, "api"));
        File.WriteAllText(Path.Combine(data, "reglamento", Doc), "# T\n\n## Plazos\n\ntexto\n");
        File.WriteAllText(Path.Combine(data, "api", "estudiantes.json"), Estudiantes);
        File.WriteAllText(Path.Combine(data, "api", "coordinadores.json"), Coordinadores);
        File.WriteAllText(Path.Combine(data, "preguntas-ejemplo.json"), "[" + Caso("E-A", "pregunta-a") + "]");
        using var api = new ApiFalsa(Guion, rutaQueEscribeLaApi);
        var s = await Ejecutar(data, raiz, api.Url, null);
        // El runner escribió su resultado en <raíz>/evals, no en el repositorio.
        Assert.True(File.Exists(Path.Combine(raiz, "evals", "resultados", "ultimo.json")));
        return s;
    }

    static void AssertEncontroLaAuditoria(Salida s)
    {
        var a = Caso(s, "E-A");
        Assert.True(a.GetProperty("auditoriaEncontrada").GetBoolean(), "No leyó la auditoría correcta:\n" + s.Texto);
        Assert.Equal([Pedido], Modelos(a));
        Assert.Equal(100, a.GetProperty("tokensEntrada").GetInt64());
        Assert.Equal(0, s.Json.GetProperty("modelo").GetProperty("sinAuditoria").GetInt32());
    }

    [Fact]
    public async Task T6g_sin_EVALS_AUDITORIA_existe_solo_la_de_la_raiz_la_usa()
    {
        var raiz = RaizTemporal();
        Sembrar(RutaRaiz(raiz), DateTime.UtcNow);
        AssertEncontroLaAuditoria(await CorreEnRaiz(raiz, RutaRaiz(raiz)));
    }

    [Fact]
    public async Task T6g_sin_EVALS_AUDITORIA_existe_solo_la_de_src_TutorFlow_Api_la_usa()
    {
        var raiz = RaizTemporal();
        Sembrar(RutaSrc(raiz), DateTime.UtcNow);
        AssertEncontroLaAuditoria(await CorreEnRaiz(raiz, RutaSrc(raiz)));
    }

    [Fact]
    public async Task T6g_sin_EVALS_AUDITORIA_con_ambas_elige_la_de_src_si_es_mas_reciente()
    {
        var raiz = RaizTemporal();
        Sembrar(RutaRaiz(raiz), DateTime.UtcNow.AddHours(-2));
        Sembrar(RutaSrc(raiz), DateTime.UtcNow);
        AssertEncontroLaAuditoria(await CorreEnRaiz(raiz, RutaSrc(raiz)));
    }

    [Fact]
    public async Task T6g_sin_EVALS_AUDITORIA_con_ambas_elige_la_de_la_raiz_si_es_mas_reciente()
    {
        var raiz = RaizTemporal();
        Sembrar(RutaSrc(raiz), DateTime.UtcNow.AddHours(-2));
        Sembrar(RutaRaiz(raiz), DateTime.UtcNow);
        AssertEncontroLaAuditoria(await CorreEnRaiz(raiz, RutaRaiz(raiz)));
    }

    [Fact]
    public async Task T6g_sin_EVALS_AUDITORIA_y_sin_ninguna_auditoria_advierte_y_no_falla_por_ello()
    {
        var raiz = RaizTemporal();
        var s = await CorreEnRaiz(raiz, null!);
        Assert.False(Caso(s, "E-A").GetProperty("auditoriaEncontrada").GetBoolean());
        Assert.Contains("ADVERTENCIA", s.Texto);
        Assert.Equal(0, s.Codigo);
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TutorFlow.Tests;

/// <summary>
/// El runner de evals registra, por pregunta, qué modelo se pidió y cuál respondió (leyendo la auditoría de la API
/// indicada en EVALS_AUDITORIA), los tokens de entrada y salida y el costo, y resume el uso separando las 403.
/// El runner corre como proceso contra un servidor HttpListener falso que también escribe la auditoría.
/// Cada corrida usa un directorio de trabajo temporal: el runner escribe evals/resultados/ultimo.json allí y no en el
/// repositorio.
/// Forma asumida de ultimo.json: casos[].{modeloPedido, modelosRespondidos, tokensEntrada, tokensSalida, costoUsd,
/// modeloDistinto, advertencia} y modelo.{modeloDistinto, rechazadas403, resto}.
/// </summary>
public sealed class EvalsModeloTests : IDisposable
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
        var d = Path.Combine(Path.GetTempPath(), "tf-evmod-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Datos y cwd sin sln arriba: el runner usa EVALS_DATA y cwd/evals.</summary>
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

    /// <summary>API falsa: responde según la pregunta y, si hay ruta, añade una línea de auditoría por petición.</summary>
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
                        : "{\"error\":\"sin permiso\"}";
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

    // Tres preguntas: A y B responden 200; B con un modelo distinto al pedido; C termina en 403 sin llamar al modelo.
    static Fila Guion(string pregunta) => pregunta switch
    {
        "pregunta-a" => new Fila(200, 100, 20, 0.001, Pedido, [Pedido]),
        "pregunta-b" => new Fila(200, 300, 40, 0.003, Pedido, ["gemini-3.6-flash"]),
        _ => new Fila(403, 0, 0, 0, null, []),
    };

    string[] TresCasos() => [Caso("E-A", "pregunta-a"), Caso("E-B", "pregunta-b"), Caso("E-C", "pregunta-c", 403)];

    // ---------- Campos por pregunta ----------

    [Fact]
    public async Task EvalsModelo_cada_pregunta_trae_modelo_pedido_respondidos_tokens_y_costo()
    {
        var (data, cwd) = Escenario(TresCasos());
        var aud = Path.Combine(NuevoDir(), "auditoria.jsonl");
        using var api = new ApiFalsa(Guion, aud);

        var s = await Ejecutar(data, cwd, api.Url, aud);

        var a = Caso(s, "E-A");
        Assert.Equal(Pedido, a.GetProperty("modeloPedido").GetString());
        Assert.Equal([Pedido], Modelos(a));
        Assert.Equal(100, a.GetProperty("tokensEntrada").GetInt64());
        Assert.Equal(20, a.GetProperty("tokensSalida").GetInt64());
        Assert.Equal(0.001, a.GetProperty("costoUsd").GetDouble(), 9);

        var b = Caso(s, "E-B");
        Assert.Equal([ "gemini-3.6-flash" ], Modelos(b));
        Assert.Equal(300, b.GetProperty("tokensEntrada").GetInt64());
        Assert.Equal(40, b.GetProperty("tokensSalida").GetInt64());
        Assert.Equal(0.003, b.GetProperty("costoUsd").GetDouble(), 9);

        var c = Caso(s, "E-C");
        Assert.Equal(JsonValueKind.Null, c.GetProperty("modeloPedido").ValueKind);
        Assert.Empty(Modelos(c));
        Assert.Equal(0, c.GetProperty("tokensEntrada").GetInt64());
        Assert.Equal(0, c.GetProperty("tokensSalida").GetInt64());
    }

    [Fact]
    public async Task EvalsModelo_cada_pregunta_imprime_su_modelo_pedido_y_respondido_en_la_salida()
    {
        var (data, cwd) = Escenario(TresCasos());
        var aud = Path.Combine(NuevoDir(), "auditoria.jsonl");
        using var api = new ApiFalsa(Guion, aud);
        var s = await Ejecutar(data, cwd, api.Url, aud);
        Assert.Contains(Pedido, s.Texto);
        Assert.Contains("gemini-3.6-flash", s.Texto);
    }

    // ---------- Modelo distinto al pedido ----------

    [Fact]
    public async Task EvalsModelo_modelo_distinto_se_marca_se_cuenta_y_no_cambia_el_codigo_de_salida()
    {
        var (data, cwd) = Escenario(TresCasos());
        var aud = Path.Combine(NuevoDir(), "auditoria.jsonl");
        using var api = new ApiFalsa(Guion, aud);
        var s = await Ejecutar(data, cwd, api.Url, aud);

        Assert.Contains("MODELO DISTINTO", s.Texto);
        Assert.True(Caso(s, "E-B").GetProperty("modeloDistinto").GetBoolean());
        Assert.False(Caso(s, "E-A").GetProperty("modeloDistinto").GetBoolean());
        Assert.Equal(1, s.Json.GetProperty("modelo").GetProperty("modeloDistinto").GetInt32());
        Assert.Equal(0, s.Codigo);   // con el mismo guion y sin distinción de modelo, la corrida aprueba
        Assert.Contains("APROBADA", s.Texto);
    }

    [Fact]
    public async Task EvalsModelo_con_el_mismo_modelo_pedido_y_respondido_no_hay_marca()
    {
        var (data, cwd) = Escenario(Caso("E-A", "pregunta-a"));
        var aud = Path.Combine(NuevoDir(), "auditoria.jsonl");
        using var api = new ApiFalsa(Guion, aud);
        var s = await Ejecutar(data, cwd, api.Url, aud);
        Assert.False(Caso(s, "E-A").GetProperty("modeloDistinto").GetBoolean());
        Assert.Equal(0, s.Json.GetProperty("modelo").GetProperty("modeloDistinto").GetInt32());
        Assert.Equal(0, s.Codigo);
    }

    // ---------- Auditoría ausente ----------

    [Fact]
    public async Task EvalsModelo_auditoria_ausente_modelo_desconocido_advertencia_y_mismo_codigo_de_salida()
    {
        var (data, cwd) = Escenario(TresCasos());
        var audReal = Path.Combine(NuevoDir(), "auditoria.jsonl");
        using var conAud = new ApiFalsa(Guion, audReal);
        var base1 = await Ejecutar(data, cwd, conAud.Url, audReal);

        var (data2, cwd2) = Escenario(TresCasos());
        var audNoExiste = Path.Combine(NuevoDir(), "no-existe.jsonl");
        using var sinAud = new ApiFalsa(Guion, null);
        var s = await Ejecutar(data2, cwd2, sinAud.Url, audNoExiste);

        Assert.False(File.Exists(audNoExiste));
        Assert.Equal(base1.Codigo, s.Codigo);
        Assert.Contains("desconocido", s.Texto);
        Assert.Contains("ADVERTENCIA", s.Texto);
        foreach (var id in new[] { "E-A", "E-B", "E-C" })
        {
            var c = Caso(s, id);
            Assert.True(c.TryGetProperty("advertencia", out var adv) && adv.ValueKind == JsonValueKind.String);
            Assert.Empty(Modelos(c));
            Assert.False(c.GetProperty("modeloDistinto").GetBoolean());
        }
        Assert.Equal(0, s.Json.GetProperty("modelo").GetProperty("modeloDistinto").GetInt32());
        Assert.Equal(3, s.Json.GetProperty("modelo").GetProperty("sinAuditoria").GetInt32());
    }

    // ---------- Resumen de uso ----------

    [Fact]
    public async Task EvalsModelo_resumen_separa_las_403_del_resto_con_tokens_promedio_costo_total_y_por_1000()
    {
        var (data, cwd) = Escenario(TresCasos());
        var aud = Path.Combine(NuevoDir(), "auditoria.jsonl");
        using var api = new ApiFalsa(Guion, aud);
        var s = await Ejecutar(data, cwd, api.Url, aud);
        var m = s.Json.GetProperty("modelo");

        var r403 = m.GetProperty("rechazadas403");
        Assert.Equal(1, r403.GetProperty("preguntas").GetInt32());
        Assert.Equal(0, r403.GetProperty("tokens").GetInt64());
        Assert.Equal(0, r403.GetProperty("costoTotalUsd").GetDouble());
        Assert.Equal(0, r403.GetProperty("costoPor1000Usd").GetDouble());

        var resto = m.GetProperty("resto");
        Assert.Equal(2, resto.GetProperty("preguntas").GetInt32());
        Assert.Equal(400, resto.GetProperty("tokensEntrada").GetInt64());
        Assert.Equal(60, resto.GetProperty("tokensSalida").GetInt64());
        Assert.Equal(460, resto.GetProperty("tokens").GetInt64());
        Assert.Equal(230, resto.GetProperty("tokensPromedio").GetDouble(), 6);
        Assert.Equal(0.004, resto.GetProperty("costoTotalUsd").GetDouble(), 9);
        Assert.Equal(2.0, resto.GetProperty("costoPor1000Usd").GetDouble(), 6);   // 0.004 / 2 * 1000

        // La salida legible lo repite.
        Assert.Contains("403", s.Texto);
        Assert.Contains("promedio", s.Texto, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1.000", s.Texto);
        Assert.Contains(0.004.ToString("F6", CultureInfo.CurrentCulture), s.Texto);
    }

    [Fact]
    public async Task EvalsModelo_la_corrida_no_deja_ultimo_json_en_el_repositorio()
    {
        var real = Path.Combine(SubirA("TutorFlow.sln"), "evals", "resultados", "ultimo.json");
        var antes = File.Exists(real) ? File.GetLastWriteTimeUtc(real) : (DateTime?)null;
        var (data, cwd) = Escenario(Caso("E-A", "pregunta-a"));
        var aud = Path.Combine(NuevoDir(), "auditoria.jsonl");
        using var api = new ApiFalsa(Guion, aud);
        await Ejecutar(data, cwd, api.Url, aud);
        var despues = File.Exists(real) ? File.GetLastWriteTimeUtc(real) : (DateTime?)null;
        Assert.Equal(antes, despues);
    }

    static string SubirA(string archivo)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, archivo))) return d.FullName;
        throw new InvalidOperationException("No se encontro " + archivo);
    }
}

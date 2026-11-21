using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TutorFlow.Tests;

/// <summary>
/// T6h: el resumen de uso del modelo en los evals separa lo gastado en preguntas SIN RESULTADO (tokensSinResultado,
/// costoSinResultadoUsd): entra en el total real pero no en el promedio por pregunta ni en el estimado por 1.000.
/// El runner corre como proceso real contra un HttpListener falso.
/// </summary>
public sealed class EvalsSinResultadoConTokensTests : IDisposable
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
        var d = Path.Combine(Path.GetTempPath(), "tf-evsin-" + Guid.NewGuid().ToString("N"));
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


    // A y B con respuesta (120 y 340 tokens; 0.001 y 0.003 USD). C: 503 en todos los reintentos, pero el modelo
    // ya había gastado 1.000 tokens (0.01 USD) según la auditoría.
    static Fila Guion(string pregunta) => pregunta switch
    {
        "pregunta-a" => new Fila(200, 100, 20, 0.001, Pedido, [Pedido]),
        "pregunta-b" => new Fila(200, 300, 40, 0.003, Pedido, [Pedido]),
        "pregunta-c" => new Fila(503, 600, 400, 0.01, Pedido, [Pedido]),
        _ => new Fila(403, 0, 0, 0, null, []),
    };

    [Fact]
    public async Task T6h_sin_resultado_con_tokens_entra_en_el_total_pero_no_en_el_promedio_ni_en_el_costo_por_1000()
    {
        var (data, cwd) = Escenario(Caso("E-A", "pregunta-a"), Caso("E-B", "pregunta-b"), Caso("E-C", "pregunta-c"));
        var aud = Path.Combine(NuevoDir(), "auditoria.jsonl");
        using var api = new ApiFalsa(Guion, aud);
        var s = await Ejecutar(data, cwd, api.Url, aud);

        var resto = s.Json.GetProperty("modelo").GetProperty("resto");
        Assert.Equal(3, resto.GetProperty("preguntas").GetInt32());
        Assert.Equal(2, resto.GetProperty("conRespuesta").GetInt32());
        Assert.Equal(1, resto.GetProperty("sinResultado").GetInt32());
        Assert.Equal(1460, resto.GetProperty("tokens").GetInt64());                       // 120 + 340 + 1000
        Assert.Equal(1000, resto.GetProperty("tokensSinResultado").GetInt64());
        Assert.Equal(0.01, resto.GetProperty("costoSinResultadoUsd").GetDouble(), 9);
        Assert.Equal(0.014, resto.GetProperty("costoTotalUsd").GetDouble(), 9);
        Assert.Equal(230, resto.GetProperty("tokensPromedio").GetDouble(), 6);            // (1460 - 1000) / 2, no 1460 / 2 = 730
        Assert.Equal(2.0, resto.GetProperty("costoPor1000Usd").GetDouble(), 6);           // (0.014 - 0.01) / 2 * 1000, no 7.0

        Assert.Contains("gastado en sin resultado", s.Texto);
        var linea = s.Texto.Split((char)10).Single(l => l.Contains("gastado en sin resultado") && l.Contains("tokens 1000"));
        Assert.Contains("1 preguntas", linea);
    }

    [Fact]
    public async Task T6h_el_runner_imprime_la_ruta_absoluta_de_la_auditoria_usada()
    {
        var (data, cwd) = Escenario(Caso("E-A", "pregunta-a"));
        var aud = Path.Combine(NuevoDir(), "auditoria.jsonl");
        using var api = new ApiFalsa(Guion, aud);
        var s = await Ejecutar(data, cwd, api.Url, aud);

        Assert.True(Path.IsPathRooted(aud));
        Assert.Contains("Archivo de auditoría: " + aud, s.Texto);
    }

    [Fact]
    public async Task T6h_sin_casos_sin_resultado_los_campos_sin_resultado_son_cero_en_ambos_grupos()
    {
        var (data, cwd) = Escenario(Caso("E-A", "pregunta-a"), Caso("E-B", "pregunta-b"), Caso("E-D", "pregunta-d", 403));
        var aud = Path.Combine(NuevoDir(), "auditoria.jsonl");
        using var api = new ApiFalsa(Guion, aud);
        var s = await Ejecutar(data, cwd, api.Url, aud);

        var m = s.Json.GetProperty("modelo");
        foreach (var grupo in new[] { "resto", "rechazadas403" })
        {
            var g = m.GetProperty(grupo);
            Assert.Equal(0, g.GetProperty("tokensSinResultado").GetInt64());
            Assert.Equal(0, g.GetProperty("costoSinResultadoUsd").GetDouble());
        }
        var resto = m.GetProperty("resto");
        Assert.Equal(460, resto.GetProperty("tokens").GetInt64());
        Assert.Equal(230, resto.GetProperty("tokensPromedio").GetDouble(), 6);
        Assert.Equal(2.0, resto.GetProperty("costoPor1000Usd").GetDouble(), 6);
    }
}

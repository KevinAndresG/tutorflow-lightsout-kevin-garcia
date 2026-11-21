using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TutorFlow.Tests;

/// <summary>
/// EVALS_CONJUNTO del runner de evals: "todos" (por defecto: preguntas-ejemplo.json + evals/casos-adicionales.json),
/// "ejemplo" (solo preguntas-ejemplo.json) y valor desconocido (error claro, código 2, sin enviar nada).
/// El runner corre como proceso real contra un HttpListener falso; cada corrida usa una raíz temporal con TutorFlow.sln
/// y sus propios casos adicionales en &lt;raíz&gt;/evals.
/// </summary>
public sealed class EvalsConjuntoTests : IDisposable
{
    const string Doc = "reglamento-x.md";
    readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var d in _dirs) { try { Directory.Delete(d, true); } catch { } }
    }

    string NuevoDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "tf-evcnj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _dirs.Add(d);
        return d;
    }

    static string Caso(string id, string pregunta) =>
        $$"""{ "id": "{{id}}", "escenario": "ESC-X", "usuario": { "rol": "estudiante", "id": "S-1" }, "pregunta": "{{pregunta}}", "codigoEsperado": 200 }""";

    // Devuelve (data, raiz): la raíz tiene TutorFlow.sln y evals/casos-adicionales.json con 2 casos propios.
    (string data, string raiz) Escenario()
    {
        var data = NuevoDir();
        Directory.CreateDirectory(Path.Combine(data, "reglamento"));
        Directory.CreateDirectory(Path.Combine(data, "api"));
        File.WriteAllText(Path.Combine(data, "reglamento", Doc), "# T\n\n## Plazos\n\ntexto\n");
        File.WriteAllText(Path.Combine(data, "api", "estudiantes.json"),
            """[ { "id": "S-1", "nombre": "Ana Uno Pérez", "programa": "Alfa", "semestre": 2 } ]""");
        File.WriteAllText(Path.Combine(data, "api", "coordinadores.json"),
            """[ { "id": "K-1", "nombre": "Laura Coord", "programa": "Alfa" } ]""");
        File.WriteAllText(Path.Combine(data, "preguntas-ejemplo.json"),
            "[" + Caso("EJ-1", "pregunta-ejemplo-1") + "," + Caso("EJ-2", "pregunta-ejemplo-2") + "]");

        var raiz = NuevoDir();
        File.WriteAllText(Path.Combine(raiz, "TutorFlow.sln"), "");
        Directory.CreateDirectory(Path.Combine(raiz, "evals"));
        File.WriteAllText(Path.Combine(raiz, "evals", "casos-adicionales.json"),
            "[" + Caso("AD-1", "pregunta-adicional-1") + "," + Caso("AD-2", "pregunta-adicional-2") + "]");
        return (data, raiz);
    }

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
        public ConcurrentQueue<string> Preguntas { get; } = new();

        public ApiFalsa()
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
                    Preguntas.Enqueue(doc.RootElement.GetProperty("pregunta").GetString()!);

                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                    {
                        respuesta = "texto normal", citas = Array.Empty<object>(), usoDatosEstudiante = false,
                        tokens = 10, costoUsd = 0.001,
                    }));
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose() { try { _l.Stop(); _l.Close(); } catch { } }
    }

    sealed record Salida(int Codigo, string Texto, JsonElement? Json);

    async Task<Salida> Ejecutar(string data, string raiz, string url, string? conjunto)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "TutorFlow.Evals.dll");
        Assert.True(File.Exists(dll), "No esta TutorFlow.Evals.dll junto a las pruebas");
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\"")
        {
            WorkingDirectory = raiz, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.Environment["EVALS_DATA"] = data;
        psi.Environment["EVALS_API_URL"] = url;
        psi.Environment["EVALS_PAUSA_MS"] = "0";
        psi.Environment["EVALS_ESPERA_MS"] = "1";
        psi.Environment["LLM_API_KEY"] = "llave-falsa-123";
        psi.Environment["EVALS_AUDITORIA"] = Path.Combine(NuevoDir(), "auditoria.jsonl");
        psi.Environment.Remove("LLM_MODEL");
        psi.Environment.Remove("EVALS_CONJUNTO");
        if (conjunto != null) psi.Environment["EVALS_CONJUNTO"] = conjunto;
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        var texto = await o + "\n" + await e;
        var ruta = Path.Combine(raiz, "evals", "resultados", "ultimo.json");
        JsonElement? json = File.Exists(ruta) ? JsonDocument.Parse(File.ReadAllText(ruta)).RootElement.Clone() : null;
        return new Salida(p.ExitCode, texto, json);
    }

    static string[] Ids(Salida s) =>
        s.Json!.Value.GetProperty("casos").EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToArray();

    [Fact]
    public async Task EVALS_CONJUNTO_ejemplo_envia_solo_las_preguntas_de_ejemplo_y_registra_conjunto()
    {
        var (data, raiz) = Escenario();
        using var api = new ApiFalsa();
        var s = await Ejecutar(data, raiz, api.Url, "ejemplo");

        Assert.Equal(["pregunta-ejemplo-1", "pregunta-ejemplo-2"], api.Preguntas.OrderBy(x => x).ToArray());
        Assert.NotNull(s.Json);
        Assert.Equal(["EJ-1", "EJ-2"], Ids(s).OrderBy(x => x).ToArray());
        Assert.Equal("ejemplo", s.Json!.Value.GetProperty("conjunto").GetString());
    }

    [Fact]
    public async Task EVALS_CONJUNTO_sin_variable_envia_todos_los_casos_y_registra_conjunto_todos()
    {
        var (data, raiz) = Escenario();
        using var api = new ApiFalsa();
        var s = await Ejecutar(data, raiz, api.Url, null);

        Assert.Equal(4, api.Preguntas.Count);
        Assert.Equal(
            ["pregunta-adicional-1", "pregunta-adicional-2", "pregunta-ejemplo-1", "pregunta-ejemplo-2"],
            api.Preguntas.OrderBy(x => x).ToArray());
        Assert.NotNull(s.Json);
        Assert.Equal(["AD-1", "AD-2", "EJ-1", "EJ-2"], Ids(s).OrderBy(x => x).ToArray());
        Assert.Equal("todos", s.Json!.Value.GetProperty("conjunto").GetString());
    }

    [Fact]
    public async Task EVALS_CONJUNTO_todos_explicito_equivale_al_valor_por_defecto()
    {
        var (data, raiz) = Escenario();
        using var api = new ApiFalsa();
        var s = await Ejecutar(data, raiz, api.Url, "todos");

        Assert.Equal(4, api.Preguntas.Count);
        Assert.NotNull(s.Json);
        Assert.Equal("todos", s.Json!.Value.GetProperty("conjunto").GetString());
    }

    [Fact]
    public async Task EVALS_CONJUNTO_desconocido_xyz_sale_con_codigo_2_sin_enviar_peticiones_ni_aprobar()
    {
        var (data, raiz) = Escenario();
        using var api = new ApiFalsa();
        var s = await Ejecutar(data, raiz, api.Url, "xyz");

        Assert.Equal(2, s.Codigo);
        Assert.Empty(api.Preguntas);
        Assert.Contains("EVALS_CONJUNTO", s.Texto);
        Assert.Contains("xyz", s.Texto);
        Assert.DoesNotContain("APROBADA", s.Texto);
        if (s.Json is { } j && j.TryGetProperty("veredicto", out var v))
            Assert.NotEqual("APROBADA", v.GetString());
    }
}

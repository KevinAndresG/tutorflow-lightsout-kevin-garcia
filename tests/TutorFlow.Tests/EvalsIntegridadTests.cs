using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TutorFlow.Evals;

namespace TutorFlow.Tests;

/// <summary>
/// Integridad del runner de evals (CA-14, CA-16): falla cerrado ante datos o casos mal formados.
/// Convenciones asumidas (a confirmar con el implementador):
///  - Corpus.Cargar lanza una excepcion (cualquier tipo) ante usuarios inconsistentes.
///  - El runner (Program) termina con codigo 2 ante un caso con campo desconocido, y el mensaje nombra el campo.
///  - Con LLM_API_KEY ausente el runner escribe un aviso que menciona LLM_API_KEY (stdout o stderr).
/// Sin red externa: el runner se ejecuta como proceso contra un servidor local falso o un puerto cerrado.
/// </summary>
public sealed class EvalsIntegridadTests : IDisposable
{
    const string Doc = "reglamento-x.md";
    readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var d in _dirs) { try { Directory.Delete(d, true); } catch { } }
    }

    string NuevoDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "tf-integ-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _dirs.Add(d);
        return d;
    }

    string Datos(params (string archivo, string json)[] api)
    {
        var dir = NuevoDir();
        Directory.CreateDirectory(Path.Combine(dir, "reglamento"));
        Directory.CreateDirectory(Path.Combine(dir, "api"));
        File.WriteAllText(Path.Combine(dir, "reglamento", Doc), "# T\n\n## Plazos\n\ntexto\n");
        foreach (var (a, j) in api) File.WriteAllText(Path.Combine(dir, "api", a), j);
        return dir;
    }

    const string Estudiantes = """
        [ { "id": "S-1", "nombre": "Ana Uno Pérez", "programa": "Alfa", "semestre": 2 },
          { "id": "S-2", "nombre": "Beto Dos Gómez", "programa": "Alfa", "semestre": 3 } ]
        """;
    const string Coordinadores = """
        [ { "id": "K-1", "nombre": "Laura Coord", "programa": "Alfa" } ]
        """;

    // ---- (1) Corpus.Cargar falla cerrado ----

    [Fact]
    public void Integridad_corpus_valido_de_control_carga()
    {
        var c = Corpus.Cargar(Datos(("estudiantes.json", Estudiantes), ("coordinadores.json", Coordinadores)));
        Assert.Equal(2, c.Estudiantes.Count);
        Assert.Single(c.Coordinadores);
    }

    [Fact]
    public void Integridad_corpus_archivo_con_formas_mezcladas_con_y_sin_semestre_es_error()
    {
        const string mezclado = """
            [ { "id": "S-1", "nombre": "Ana Uno Pérez", "programa": "Alfa", "semestre": 2 },
              { "id": "S-2", "nombre": "Beto Dos Gómez", "programa": "Alfa" } ]
            """;
        var dir = Datos(("usuarios.json", mezclado));
        Assert.ThrowsAny<Exception>(() => Corpus.Cargar(dir));
    }

    [Fact]
    public void Integridad_corpus_mismo_id_en_dos_roles_es_error()
    {
        const string coordConIdDeEstudiante = """
            [ { "id": "S-1", "nombre": "Laura Coord", "programa": "Alfa" } ]
            """;
        var dir = Datos(("estudiantes.json", Estudiantes), ("coordinadores.json", coordConIdDeEstudiante));
        Assert.ThrowsAny<Exception>(() => Corpus.Cargar(dir));
    }

    [Fact]
    public void Integridad_corpus_sin_ningun_estudiante_es_error()
    {
        var dir = Datos(("coordinadores.json", Coordinadores));
        Assert.ThrowsAny<Exception>(() => Corpus.Cargar(dir));
    }

    [Fact]
    public void Integridad_corpus_sin_ningun_coordinador_es_error()
    {
        var dir = Datos(("estudiantes.json", Estudiantes));
        Assert.ThrowsAny<Exception>(() => Corpus.Cargar(dir));
    }

    [Fact]
    public void Integridad_corpus_sin_usuarios_es_error()
    {
        var dir = Datos();
        Assert.ThrowsAny<Exception>(() => Corpus.Cargar(dir));
    }

    [Fact]
    public void Integridad_corpus_real_de_data_carga_con_ambos_roles()
    {
        var raiz = SubirA("TutorFlow.sln");
        var c = Corpus.Cargar(Path.Combine(raiz, "data"));
        Assert.NotEmpty(c.Estudiantes);
        Assert.NotEmpty(c.Coordinadores);
        Assert.NotEmpty(c.Citas);
    }

    // ---- (2) Respuesta vacia en un 200 falla ----

    static string Cuerpo(string respuesta) => JsonSerializer.Serialize(new
    {
        respuesta,
        citas = Array.Empty<object>(),
        usoDatosEstudiante = false,
        tokens = 1,
        costoUsd = 0.0,
    });

    Corpus CorpusBase() => Corpus.Cargar(Datos(("estudiantes.json", Estudiantes), ("coordinadores.json", Coordinadores)));

    static Caso Laxo(Action<Caso>? cfg = null)
    {
        var c = new Caso { Id = "L", Escenario = "ESC-X", Usuario = new("estudiante", "S-1"), Pregunta = "p", CodigoEsperado = 200 };
        cfg?.Invoke(c);
        return c;
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t  \r\n")]
    public void Integridad_200_con_respuesta_vacia_o_en_blanco_falla_en_caso_laxo(string respuesta)
    {
        var r = Evaluador.Evaluar(Laxo(), new RespuestaHttp(200, Cuerpo(respuesta)), CorpusBase(), null);
        Assert.Equal(Estado.Falla, r.Estado);
        Assert.NotEmpty(r.Motivos);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Integridad_200_vacia_falla_tambien_con_palabras_clave_vacias_y_citas_vacias(string respuesta)
    {
        var c = Laxo(x => { x.PalabrasClave = new(); x.CitasEsperadas = new(); x.UsoDatosEstudianteEsperado = false; });
        Assert.Equal(Estado.Falla, Evaluador.Evaluar(c, new RespuestaHttp(200, Cuerpo(respuesta)), CorpusBase(), null).Estado);
    }

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    public void Integridad_caso_solo_con_palabras_prohibidas_no_aprueba_respuesta_vacia(string respuesta)
    {
        var c = Laxo(x => x.PalabrasProhibidas = new() { "artículo 999" });
        Assert.Equal(Estado.Falla, Evaluador.Evaluar(c, new RespuestaHttp(200, Cuerpo(respuesta)), CorpusBase(), null).Estado);
    }

    [Fact]
    public void Integridad_caso_solo_con_palabras_prohibidas_aprueba_respuesta_con_texto_normal()
    {
        var c = Laxo(x => x.PalabrasProhibidas = new() { "artículo 999" });
        var r = Evaluador.Evaluar(c, new RespuestaHttp(200, Cuerpo("No existe ese artículo en el reglamento.")), CorpusBase(), null);
        Assert.Equal(Estado.Ok, r.Estado);
        Assert.Empty(r.Motivos);
    }

    [Fact]
    public void Integridad_respuesta_vacia_en_403_no_se_penaliza_como_200()
    {
        var c = Laxo(x => x.CodigoEsperado = 403);
        Assert.Equal(Estado.Ok, Evaluador.Evaluar(c, new RespuestaHttp(403, "{}"), CorpusBase(), null).Estado);
    }

    // ---- Runner como proceso ----

    static string SubirA(string archivo)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, archivo))) return d.FullName;
        throw new InvalidOperationException("No se encontro " + archivo);
    }

    sealed record Salida(int Codigo, string Texto);

    static async Task<Salida> EjecutarRunner(string dataDir, string cwd, string url, string? llave)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "TutorFlow.Evals.dll");
        Assert.True(File.Exists(dll), "No esta TutorFlow.Evals.dll junto a las pruebas");
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\"")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["EVALS_DATA"] = dataDir;
        psi.Environment["EVALS_API_URL"] = url;
        psi.Environment["EVALS_PAUSA_MS"] = "0";
        psi.Environment["EVALS_ESPERA_MS"] = "1";
        psi.Environment.Remove("LLM_API_KEY");
        if (llave is not null) psi.Environment["LLM_API_KEY"] = llave;
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        return new Salida(p.ExitCode, await o + "\n" + await e);
    }

    static int PuertoLibre()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Servidor falso que siempre responde 200 con la respuesta dada.</summary>
    sealed class ServidorFalso : IDisposable
    {
        readonly HttpListener _l = new();
        readonly Task _bucle;
        public string Url { get; }

        public ServidorFalso(string cuerpo)
        {
            var port = PuertoLibre();
            Url = $"http://localhost:{port}";
            _l.Prefixes.Add(Url + "/");
            _l.Start();
            _bucle = Task.Run(async () =>
            {
                while (_l.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _l.GetContextAsync(); } catch { break; }
                    var bytes = Encoding.UTF8.GetBytes(cuerpo);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose() { try { _l.Stop(); _l.Close(); } catch { } }
    }

    /// <summary>Prepara datos y cwd sin sln arriba: el runner usa EVALS_DATA y cwd/evals.</summary>
    (string data, string cwd) Escenario(string preguntasEjemplo, string? adicionales = null)
    {
        var data = Datos(("estudiantes.json", Estudiantes), ("coordinadores.json", Coordinadores));
        File.WriteAllText(Path.Combine(data, "preguntas-ejemplo.json"), preguntasEjemplo);
        var cwd = NuevoDir();
        if (adicionales is not null)
        {
            Directory.CreateDirectory(Path.Combine(cwd, "evals"));
            File.WriteAllText(Path.Combine(cwd, "evals", "casos-adicionales.json"), adicionales);
        }
        return (data, cwd);
    }

    const string CasoValido = """
        { "id": "V-1", "escenario": "ESC-X", "usuario": { "rol": "estudiante", "id": "S-1" },
          "pregunta": "hola", "codigoEsperado": 200, "palabrasProhibidas": ["artículo 999"] }
        """;

    // ---- (3) Campos desconocidos en casos ----

    [Theory]
    [InlineData("palabrasProhibida")]
    [InlineData("palabrasClaves")]
    [InlineData("codigoEsperdo")]
    public async Task Integridad_runner_campo_desconocido_en_caso_da_error_claro_y_codigo_2(string campoMalo)
    {
        var caso = CasoValido.Replace("\"pregunta\"", $"\"{campoMalo}\": [\"x\"], \"pregunta\"");
        var (data, cwd) = Escenario("[" + caso + "]");
        using var srv = new ServidorFalso(Cuerpo("texto normal"));
        var s = await EjecutarRunner(data, cwd, srv.Url, "llave-falsa-123");
        Assert.Equal(2, s.Codigo);
        Assert.Contains(campoMalo, s.Texto);
        Assert.DoesNotContain("APROBADA", s.Texto.Replace("REPROBADA", "").Replace("INCONCLUSA", ""));
    }

    [Fact]
    public async Task Integridad_runner_campo_desconocido_en_casos_adicionales_tambien_da_codigo_2()
    {
        var malo = CasoValido.Replace("\"id\": \"V-1\"", "\"id\": \"V-2\", \"palabrasProhibida\": [\"x\"]");
        var (data, cwd) = Escenario("[" + CasoValido + "]", "[" + malo + "]");
        using var srv = new ServidorFalso(Cuerpo("texto normal"));
        var s = await EjecutarRunner(data, cwd, srv.Url, "llave-falsa-123");
        Assert.Equal(2, s.Codigo);
        Assert.Contains("palabrasProhibida", s.Texto);
    }

    [Fact]
    public async Task Integridad_runner_campo_nota_informativo_se_acepta_y_la_corrida_aprueba()
    {
        var caso = CasoValido.Replace("\"pregunta\"", "\"nota\": \"texto informativo\", \"pregunta\"");
        var (data, cwd) = Escenario("[" + caso + "]");
        using var srv = new ServidorFalso(Cuerpo("texto normal"));
        var s = await EjecutarRunner(data, cwd, srv.Url, "llave-falsa-123");
        Assert.Equal(0, s.Codigo);
        Assert.Contains("APROBADA", s.Texto);
    }

    [Fact]
    public async Task Integridad_runner_respuesta_vacia_con_caso_laxo_reprueba_la_corrida()
    {
        var (data, cwd) = Escenario("[" + CasoValido + "]");
        using var srv = new ServidorFalso(Cuerpo("   "));
        var s = await EjecutarRunner(data, cwd, srv.Url, "llave-falsa-123");
        Assert.Equal(1, s.Codigo);
    }

    [Fact]
    public async Task Integridad_runner_carga_el_archivo_real_casos_adicionales_sin_rechazarlo()
    {
        var real = Path.Combine(SubirA("TutorFlow.sln"), "evals", "casos-adicionales.json");
        var json = File.ReadAllText(real);
        var esperados = JsonDocument.Parse(json).RootElement.GetArrayLength();
        var (data, cwd) = Escenario("[]", json);
        // El servidor falso responde 200 trivial: la mayoria de casos reprueba (codigo 1), pero todos deben cargarse.
        using var srv = new ServidorFalso(Cuerpo("texto normal"));
        var s = await EjecutarRunner(data, cwd, srv.Url, "llave-falsa-123");
        Assert.Contains($"{esperados} casos", s.Texto);
        Assert.NotEqual(2, s.Codigo);
    }

    [Fact]
    public void Integridad_archivo_real_de_casos_adicionales_deserializa_con_el_modelo_Caso()
    {
        var real = Path.Combine(SubirA("TutorFlow.sln"), "evals", "casos-adicionales.json");
        var lista = JsonSerializer.Deserialize<List<Caso>>(File.ReadAllText(real), Evaluador.JsonOpts);
        Assert.NotNull(lista);
        Assert.NotEmpty(lista!);
        Assert.All(lista!, c => Assert.False(string.IsNullOrWhiteSpace(c.Id)));
        var a10 = Assert.Single(lista!, c => c.Id == "A-10");
        Assert.True(a10.CitasEsperadas is null || a10.CitasEsperadas.Count == 0);
        Assert.Contains("artículo 999", a10.PalabrasProhibidas!);
    }

    // ---- (4) LLM_API_KEY ausente ----

    [Fact]
    public async Task Integridad_runner_sin_LLM_API_KEY_avisa()
    {
        var (data, cwd) = Escenario("[" + CasoValido + "]");
        using var srv = new ServidorFalso(Cuerpo("texto normal"));
        var s = await EjecutarRunner(data, cwd, srv.Url, null);
        Assert.Contains("LLM_API_KEY", s.Texto);
    }

    [Fact]
    public async Task Integridad_runner_con_llave_no_avisa_ni_imprime_su_valor()
    {
        var (data, cwd) = Escenario("[" + CasoValido + "]");
        using var srv = new ServidorFalso(Cuerpo("texto normal"));
        var s = await EjecutarRunner(data, cwd, srv.Url, "llave-falsa-123");
        Assert.DoesNotContain("LLM_API_KEY", s.Texto);
        Assert.DoesNotContain("llave-falsa-123", s.Texto);
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static TutorFlow.Tests.AuditoriaAsserts;

namespace TutorFlow.Tests;

/// <summary>
/// ESC-05.7 (respaldos del proveedor) y SPEC 4 (variables LLM_RESPALDO_N_*), caja negra por HTTP. Todos los proveedores
/// pasan por el HttpClient "llm"; el manejador falso despacha por host y nunca usa red.
/// </summary>
public class RespaldosLlmTests
{
    private const string Cita = "{\"documento\":\"reglamento-prueba.md\",\"seccion\":\"Nota mínima\"}";
    private const string FinalJson = "{\"respuesta\":\"La nota mínima es 3.0.\",\"citas\":[" + Cita + "]}";
    private const string LlavePrincipal = "sk-PRINCIPAL-aaaa1111-secreta";
    private const string LlaveR1 = "sk-RESPALDO1-bbbb2222-secreta";
    private const string LlaveR2 = "sk-RESPALDO2-cccc3333-secreta";
    private const string LlaveR5 = "sk-RESPALDO5-dddd4444-secreta";
    private static readonly string[] Llaves = [LlavePrincipal, LlaveR1, LlaveR2, LlaveR5];

    // ---------- Manejador falso por host ----------

    private sealed class PorHost : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<Task<HttpResponseMessage>>> _rutas = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<(string Auth, JsonNode Cuerpo)>> Peticiones { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> NoRegistrados { get; } = [];

        public PorHost Ruta(string host, Func<Task<HttpResponseMessage>> f) { _rutas[host] = f; return this; }
        public PorHost Ruta(string host, HttpStatusCode codigo) => Ruta(host, () => Task.FromResult(Estado(codigo)));
        public PorHost RutaOk(string host, int entrada = 1000, int salida = 500) => Ruta(host, () => Task.FromResult(Ok(entrada, salida)));

        public int Cuenta(string host) { lock (Peticiones) return Peticiones.TryGetValue(host, out var l) ? l.Count : 0; }
        public int Total { get { lock (Peticiones) return Peticiones.Values.Sum(l => l.Count); } }

        public static HttpResponseMessage Estado(HttpStatusCode c) =>
            new(c) { Content = new StringContent("{\"error\":\"fallo-del-proveedor\"}", Encoding.UTF8, "application/json") };

        public static HttpResponseMessage Ok(int entrada, int salida) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = FinalJson },
                    ["finish_reason"] = "stop",
                }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = entrada, ["completion_tokens"] = salida, ["total_tokens"] = entrada + salida },
            }.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var host = request.RequestUri!.Host;
            var auth = request.Headers.TryGetValues("Authorization", out var a) ? string.Join(",", a) : "";
            var cuerpo = request.Content == null ? new JsonObject() : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct))!;
            lock (Peticiones)
            {
                if (!Peticiones.TryGetValue(host, out var l)) Peticiones[host] = l = [];
                l.Add((auth, cuerpo));
            }
            Func<Task<HttpResponseMessage>>? f;
            lock (_rutas) _rutas.TryGetValue(host, out f);
            if (f == null)
            {
                lock (NoRegistrados) NoRegistrados.Add(host);
                throw new HttpRequestException("host sin ruta: " + host);
            }
            return await f();
        }
    }

    // ---------- Entorno ----------

    private sealed class Entorno : IDisposable
    {
        private readonly WebApplicationFactory<Program> _fabrica;
        public PorHost Handler { get; }
        public LogsEnMemoria Logs { get; } = new();
        public string RutaAuditoria { get; } = Path.Combine(Path.GetTempPath(), "tf-resp-" + Guid.NewGuid().ToString("N") + ".jsonl");
        public HttpClient Cliente { get; }

        public Entorno(PorHost handler, IDictionary<string, string?> config)
        {
            Handler = handler;
            var cfg = new Dictionary<string, string?>
            {
                ["Datos:Ruta"] = Fabrica.RutaFixtures,
                ["Auditoria:Ruta"] = RutaAuditoria,
            };
            foreach (var (k, v) in config) cfg[k] = v;
            _fabrica = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                foreach (var (k, v) in cfg) b.UseSetting(k, v);
                b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(cfg));
                b.ConfigureServices(sv => sv.AddLogging(l => { l.SetMinimumLevel(LogLevel.Trace); l.AddProvider(Logs); }));
                b.ConfigureTestServices(sv => sv.AddHttpClient("llm").ConfigurePrimaryHttpMessageHandler(() => handler));
            });
            Cliente = _fabrica.CreateClient();   // arranca el host: una configuración inválida lanza aquí
            Cliente.Timeout = TimeSpan.FromSeconds(60);
        }

        public async Task<(HttpStatusCode Codigo, string Cuerpo)> Preguntar(string rol = "estudiante", string id = "T-E1",
            string pregunta = "¿Cuál es la nota mínima?")
        {
            var json = JsonSerializer.Serialize(new { pregunta, usuario = new { rol, id } });
            var r = await Cliente.PostAsync("/api/asistente/preguntar", new StringContent(json, Encoding.UTF8, "application/json"));
            return (r.StatusCode, await r.Content.ReadAsStringAsync());
        }

        public string TextoAuditoria()
        {
            if (!File.Exists(RutaAuditoria)) return "";
            using var fs = new FileStream(RutaAuditoria, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            return sr.ReadToEnd();
        }

        private List<string> Lineas() =>
            TextoAuditoria().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();

        public async Task<JsonElement> UnaLinea()
        {
            var fin = DateTime.UtcNow.AddSeconds(5);
            while (Lineas().Count < 1 && DateTime.UtcNow < fin) await Task.Delay(50);
            await Task.Delay(100);   // margen para detectar líneas duplicadas escritas tarde
            var lineas = Lineas();
            Assert.Single(lineas);
            return JsonDocument.Parse(lineas[0]).RootElement.Clone();
        }

        public void Dispose()
        {
            Cliente.Dispose();
            _fabrica.Dispose();
            try { if (File.Exists(RutaAuditoria)) File.Delete(RutaAuditoria); } catch { }
        }
    }

    private static Dictionary<string, string?> Principal(string precioE = "1", string precioS = "2") => new()
    {
        ["LLM_API_KEY"] = LlavePrincipal,
        ["LLM_BASE_URL"] = "http://principal.test/v1",
        ["LLM_MODEL"] = "modelo-principal",
        ["LLM_PRECIO_ENTRADA_MTOK"] = precioE,
        ["LLM_PRECIO_SALIDA_MTOK"] = precioS,
    };

    private static void Respaldo(IDictionary<string, string?> cfg, int n, string host, string llave, string modelo,
        string precioE, string precioS)
    {
        var p = $"LLM_RESPALDO_{n}_";
        cfg[p + "API_KEY"] = llave;
        cfg[p + "PROVIDER"] = "openai";
        cfg[p + "BASE_URL"] = $"http://{host}/v1";
        cfg[p + "MODEL"] = modelo;
        cfg[p + "PRECIO_ENTRADA_MTOK"] = precioE;
        cfg[p + "PRECIO_SALIDA_MTOK"] = precioS;
    }

    private static (string Proveedor, string Resultado)[] Intentos(JsonElement l)
    {
        Assert.True(l.TryGetProperty("intentos", out var i), "falta el campo intentos");
        Assert.Equal(JsonValueKind.Array, i.ValueKind);
        return i.EnumerateArray()
            .Select(x => (x.GetProperty("proveedor").GetString()!, x.GetProperty("resultado").GetString()!)).ToArray();
    }

    private static string? Proveedor(JsonElement l)
    {
        Assert.True(l.TryGetProperty("proveedor", out var p), "falta el campo proveedor");
        return p.ValueKind == JsonValueKind.Null ? null : p.GetString();
    }

    private static void AssertSinLlaves(Entorno e, string cuerpoRespuesta)
    {
        foreach (var k in Llaves)
        {
            Assert.DoesNotContain(k, cuerpoRespuesta);
            Assert.DoesNotContain(k, e.TextoAuditoria());
            Assert.DoesNotContain(k, e.Logs.Todo);
        }
    }

    private static void AssertCuerpoError(string cuerpo)
    {
        using var d = JsonDocument.Parse(cuerpo);
        Assert.Equal(JsonValueKind.String, d.RootElement.GetProperty("error").ValueKind);
    }

    // ---------- Casos ----------

    [Fact]
    public async Task ESC_05_7_principal_429_responde_el_respaldo_con_su_precio_y_lo_audita()
    {
        var cfg = Principal("1", "2");
        Respaldo(cfg, 1, "r1.test", LlaveR1, "modelo-r1", "3", "6");
        var h = new PorHost().Ruta("principal.test", HttpStatusCode.TooManyRequests).RutaOk("r1.test", 1000, 500);
        using var e = new Entorno(h, cfg);

        var (c, b) = await e.Preguntar();
        Assert.Equal(HttpStatusCode.OK, c);
        using var d = JsonDocument.Parse(b);
        Assert.Equal("La nota mínima es 3.0.", d.RootElement.GetProperty("respuesta").GetString());
        Assert.Equal(1500, d.RootElement.GetProperty("tokens").GetInt64());
        // (1000*3 + 500*6) / 1e6 = 0.006 con el precio del respaldo (con el del principal serían 0.002).
        Assert.Equal(0.006, d.RootElement.GetProperty("costoUsd").GetDouble(), 9);
        Assert.Empty(h.NoRegistrados);

        Assert.Equal(1, h.Cuenta("principal.test"));
        Assert.Equal(1, h.Cuenta("r1.test"));
        var pr = h.Peticiones["principal.test"][0];
        var r1 = h.Peticiones["r1.test"][0];
        Assert.Contains(LlavePrincipal, pr.Auth);
        Assert.Equal("modelo-principal", pr.Cuerpo["model"]!.GetValue<string>());
        Assert.Contains(LlaveR1, r1.Auth);
        Assert.DoesNotContain(LlavePrincipal, r1.Auth);
        Assert.Equal("modelo-r1", r1.Cuerpo["model"]!.GetValue<string>());

        var l = await e.UnaLinea();
        AssertForma(l);
        Assert.Equal(200, Codigo(l));
        Assert.Equal(1500, Tokens(l));
        Assert.Equal(0.006, Costo(l), 9);
        Assert.Equal("respaldo-1", Proveedor(l));
        Assert.Equal([("principal", "429"), ("respaldo-1", "ok")], Intentos(l));
        AssertSinLlaves(e, b);
    }

    [Fact]
    public async Task ESC_05_7_principal_ok_no_toca_el_respaldo_y_audita_proveedor_principal()
    {
        var cfg = Principal("1", "2");
        Respaldo(cfg, 1, "r1.test", LlaveR1, "modelo-r1", "3", "6");
        var h = new PorHost().RutaOk("principal.test", 1000, 500).RutaOk("r1.test");
        using var e = new Entorno(h, cfg);

        var (c, b) = await e.Preguntar();
        Assert.Equal(HttpStatusCode.OK, c);
        using var d = JsonDocument.Parse(b);
        Assert.Equal(0.002, d.RootElement.GetProperty("costoUsd").GetDouble(), 9);
        Assert.Equal(0, h.Cuenta("r1.test"));
        var l = await e.UnaLinea();
        Assert.Equal("principal", Proveedor(l));
        Assert.Equal([("principal", "ok")], Intentos(l));
    }

    [Fact]
    public async Task ESC_05_7_principal_503_salta_al_respaldo()
    {
        var cfg = Principal();
        Respaldo(cfg, 1, "r1.test", LlaveR1, "modelo-r1", "3", "6");
        var h = new PorHost().Ruta("principal.test", HttpStatusCode.ServiceUnavailable).RutaOk("r1.test");
        using var e = new Entorno(h, cfg);

        var (c, b) = await e.Preguntar();
        Assert.Equal(HttpStatusCode.OK, c);
        var l = await e.UnaLinea();
        Assert.Equal("respaldo-1", Proveedor(l));
        Assert.Equal([("principal", "503"), ("respaldo-1", "ok")], Intentos(l));
        AssertSinLlaves(e, b);
    }

    [Fact]
    public async Task ESC_05_7_tiempo_agotado_del_principal_salta_al_respaldo()
    {
        var cfg = Principal();
        Respaldo(cfg, 1, "r1.test", LlaveR1, "modelo-r1", "3", "6");
        // El plazo de 60 s no se puede acortar: se simula lo que lanza HttpClient al vencer su Timeout.
        var h = new PorHost()
            .Ruta("principal.test", () => throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()))
            .RutaOk("r1.test");
        using var e = new Entorno(h, cfg);

        var (c, b) = await e.Preguntar();
        Assert.Equal(HttpStatusCode.OK, c);
        var l = await e.UnaLinea();
        Assert.Equal("respaldo-1", Proveedor(l));
        Assert.Equal([("principal", "tiempo"), ("respaldo-1", "ok")], Intentos(l));
        AssertSinLlaves(e, b);
    }

    [Fact]
    public async Task ESC_05_7_principal_400_salta_al_respaldo()
    {
        var cfg = Principal();
        Respaldo(cfg, 1, "r1.test", LlaveR1, "modelo-r1", "3", "6");
        var h = new PorHost().Ruta("principal.test", HttpStatusCode.BadRequest).RutaOk("r1.test");
        using var e = new Entorno(h, cfg);

        var (c, b) = await e.Preguntar();
        Assert.Equal(HttpStatusCode.OK, c);
        Assert.Equal(1, h.Cuenta("principal.test"));
        Assert.Equal(1, h.Cuenta("r1.test"));
        var l = await e.UnaLinea();
        Assert.Equal(200, Codigo(l));
        Assert.Equal("respaldo-1", Proveedor(l));
        Assert.Equal([("principal", "400"), ("respaldo-1", "ok")], Intentos(l));
        AssertSinLlaves(e, b);
    }

    [Fact]
    public async Task ESC_05_7_principal_400_tool_use_failed_salta_al_respaldo()
    {
        var cfg = Principal();
        Respaldo(cfg, 1, "r1.test", LlaveR1, "modelo-r1", "3", "6");
        var h = new PorHost().Ruta("principal.test", () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\":{\"message\":\"Failed to call a function. Please adjust your prompt.\",\"type\":\"invalid_request_error\",\"code\":\"tool_use_failed\",\"failed_generation\":\"<function=consultar_notas>{}</function>\"}}",
                Encoding.UTF8, "application/json"),
        })).RutaOk("r1.test");
        using var e = new Entorno(h, cfg);

        var (c, b) = await e.Preguntar();
        Assert.Equal(HttpStatusCode.OK, c);
        Assert.Equal(1, h.Cuenta("r1.test"));
        var l = await e.UnaLinea();
        Assert.Equal("respaldo-1", Proveedor(l));
        Assert.Equal([("principal", "400"), ("respaldo-1", "ok")], Intentos(l));
        AssertSinLlaves(e, b);
    }

    [Fact]
    public async Task ESC_05_7_todos_los_proveedores_400_da_503_con_todos_los_intentos()
    {
        var cfg = Principal();
        Respaldo(cfg, 1, "r1.test", LlaveR1, "modelo-r1", "3", "6");
        var h = new PorHost().Ruta("principal.test", HttpStatusCode.BadRequest).Ruta("r1.test", HttpStatusCode.BadRequest);
        using var e = new Entorno(h, cfg);

        var (c, b) = await e.Preguntar();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, c);
        AssertCuerpoError(b);
        var l = await e.UnaLinea();
        Assert.Equal(503, Codigo(l));
        Assert.Null(Proveedor(l));
        Assert.Equal([("principal", "400"), ("respaldo-1", "400")], Intentos(l));
        AssertSinLlaves(e, b);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task ESC_05_7_principal_401_no_salta_al_respaldo_y_da_503(HttpStatusCode codigoPrincipal)
    {
        var cfg = Principal();
        Respaldo(cfg, 1, "r1.test", LlaveR1, "modelo-r1", "3", "6");
        var h = new PorHost().Ruta("principal.test", codigoPrincipal).RutaOk("r1.test");
        using var e = new Entorno(h, cfg);

        var (c, b) = await e.Preguntar();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, c);
        AssertCuerpoError(b);
        Assert.Equal(1, h.Cuenta("principal.test"));
        Assert.Equal(0, h.Cuenta("r1.test"));

        var l = await e.UnaLinea();
        AssertForma(l);
        Assert.Equal(503, Codigo(l));
        Assert.Equal(0, Tokens(l));
        Assert.Equal(0d, Costo(l));
        Assert.Null(Proveedor(l));
        var intentos = Intentos(l);
        Assert.Single(intentos);
        Assert.Equal("principal", intentos[0].Proveedor);
        Assert.Contains(intentos[0].Resultado, new[] { "429", "503", "tiempo", "error" });
        AssertSinLlaves(e, b);
    }

    [Fact]
    public async Task ESC_05_7_todos_los_proveedores_429_da_503_con_todos_los_intentos()
    {
        var cfg = Principal();
        Respaldo(cfg, 1, "r1.test", LlaveR1, "modelo-r1", "3", "6");
        Respaldo(cfg, 2, "r2.test", LlaveR2, "modelo-r2", "4", "8");
        var h = new PorHost().Ruta("principal.test", HttpStatusCode.TooManyRequests)
            .Ruta("r1.test", HttpStatusCode.TooManyRequests).Ruta("r2.test", HttpStatusCode.TooManyRequests);
        using var e = new Entorno(h, cfg);

        var (c, b) = await e.Preguntar();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, c);
        AssertCuerpoError(b);
        var l = await e.UnaLinea();
        AssertForma(l);
        Assert.Equal(503, Codigo(l));
        Assert.Equal(0, Tokens(l));
        Assert.Equal(0d, Costo(l));
        Assert.Null(Proveedor(l));
        Assert.Equal([("principal", "429"), ("respaldo-1", "429"), ("respaldo-2", "429")], Intentos(l));
        AssertSinLlaves(e, b);
    }

    [Fact]
    public async Task ESC_05_7_hueco_de_numeracion_respaldo_sin_llave_se_ignora_y_se_siguen_leyendo_los_demas()
    {
        var cfg = Principal();
        // RESPALDO_1 con todo menos la llave: no existe como respaldo.
        Respaldo(cfg, 1, "r1.test", "", "modelo-r1", "3", "6");
        Respaldo(cfg, 2, "r2.test", LlaveR2, "modelo-r2", "4", "8");
        Respaldo(cfg, 5, "r5.test", LlaveR5, "modelo-r5", "5", "10");
        var h = new PorHost().Ruta("principal.test", HttpStatusCode.TooManyRequests)
            .Ruta("r1.test", HttpStatusCode.OK).Ruta("r2.test", HttpStatusCode.TooManyRequests).RutaOk("r5.test", 1000, 500);
        using var e = new Entorno(h, cfg);

        var (c, b) = await e.Preguntar();
        Assert.Equal(HttpStatusCode.OK, c);
        using var d = JsonDocument.Parse(b);
        // (1000*5 + 500*10) / 1e6 = 0.01
        Assert.Equal(0.01, d.RootElement.GetProperty("costoUsd").GetDouble(), 9);
        Assert.Equal(0, h.Cuenta("r1.test"));
        Assert.Equal(1, h.Cuenta("r2.test"));
        Assert.Equal(1, h.Cuenta("r5.test"));

        var l = await e.UnaLinea();
        Assert.Equal("respaldo-5", Proveedor(l));
        Assert.Equal([("principal", "429"), ("respaldo-2", "429"), ("respaldo-5", "ok")], Intentos(l));
        AssertSinLlaves(e, b);
    }

    [Fact]
    public async Task ESC_05_7_respaldo_con_valores_vacios_arranca_con_los_valores_por_defecto()
    {
        var cfg = Principal();
        cfg["LLM_RESPALDO_1_API_KEY"] = LlaveR1;
        cfg["LLM_RESPALDO_1_PROVIDER"] = "";
        cfg["LLM_RESPALDO_1_BASE_URL"] = "";
        cfg["LLM_RESPALDO_1_MODEL"] = "";
        cfg["LLM_RESPALDO_1_PRECIO_ENTRADA_MTOK"] = "";
        cfg["LLM_RESPALDO_1_PRECIO_SALIDA_MTOK"] = "";
        // BASE_URL vacía = endpoint por defecto del proveedor (Gemini, formato OpenAI).
        var h = new PorHost().Ruta("principal.test", HttpStatusCode.TooManyRequests)
            .RutaOk("generativelanguage.googleapis.com", 1000, 500);
        using var e = new Entorno(h, cfg);

        var (c, b) = await e.Preguntar();
        Assert.Equal(HttpStatusCode.OK, c);
        using var d = JsonDocument.Parse(b);
        Assert.True(d.RootElement.GetProperty("costoUsd").GetDouble() >= 0);
        Assert.Equal(1, h.Cuenta("generativelanguage.googleapis.com"));
        Assert.Contains(LlaveR1, h.Peticiones["generativelanguage.googleapis.com"][0].Auth);
        var l = await e.UnaLinea();
        Assert.Equal("respaldo-1", Proveedor(l));
        AssertSinLlaves(e, b);
    }

    [Theory]
    [InlineData("LLM_RESPALDO_1_PRECIO_ENTRADA_MTOK")]
    [InlineData("LLM_RESPALDO_1_PRECIO_SALIDA_MTOK")]
    [InlineData("LLM_PRECIO_SALIDA_MTOK")]
    [InlineData("LLM_PRECIO_ENTRADA_MTOK")]
    public void ESC_05_7_precio_no_numerico_impide_arrancar_y_nombra_la_variable(string variable)
    {
        var cfg = Principal();
        Respaldo(cfg, 1, "r1.test", LlaveR1, "modelo-r1", "3", "6");
        cfg[variable] = "abc";
        var ex = Record.Exception(() => new Entorno(new PorHost(), cfg).Dispose());
        Assert.NotNull(ex);
        var texto = ex!.ToString();
        Assert.Contains(variable, texto);
        foreach (var k in Llaves) Assert.DoesNotContain(k, texto);
    }

    [Fact]
    public void ESC_05_7_provider_invalido_en_un_respaldo_con_llave_impide_arrancar_y_nombra_la_variable()
    {
        var cfg = Principal();
        Respaldo(cfg, 3, "r3.test", LlaveR2, "modelo-r3", "3", "6");
        cfg["LLM_RESPALDO_3_PROVIDER"] = "no-existe";
        var ex = Record.Exception(() => new Entorno(new PorHost(), cfg).Dispose());
        Assert.NotNull(ex);
        var texto = ex!.ToString();
        Assert.Contains("LLM_RESPALDO_3_PROVIDER", texto);
        foreach (var k in Llaves) Assert.DoesNotContain(k, texto);
    }

    [Fact]
    public async Task ESC_05_7_salud_responde_200_aunque_todos_los_proveedores_fallen()
    {
        var cfg = Principal();
        Respaldo(cfg, 1, "r1.test", LlaveR1, "modelo-r1", "3", "6");
        var h = new PorHost().Ruta("principal.test", HttpStatusCode.TooManyRequests).Ruta("r1.test", HttpStatusCode.TooManyRequests);
        using var e = new Entorno(h, cfg);
        await e.Preguntar();
        var r = await e.Cliente.GetAsync("/api/salud");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("{\"estado\":\"ok\"}", (await r.Content.ReadAsStringAsync()).Replace(" ", ""));
    }

    [Fact]
    public async Task ESC_05_7_un_403_no_llama_a_ningun_proveedor()
    {
        var cfg = Principal();
        Respaldo(cfg, 1, "r1.test", LlaveR1, "modelo-r1", "3", "6");
        var h = new PorHost().RutaOk("principal.test").RutaOk("r1.test");
        using var e = new Entorno(h, cfg);

        var (c, _) = await e.Preguntar(rol: "coordinador", id: "T-E1");   // rol cruzado (RN-08)
        Assert.Equal(HttpStatusCode.Forbidden, c);
        Assert.Equal(0, h.Total);
        var l = await e.UnaLinea();
        Assert.Equal(403, Codigo(l));
        Assert.Equal(0, Tokens(l));
        Assert.Equal(0d, Costo(l));
        Assert.Null(Proveedor(l));
    }

    [Fact]
    public async Task ESC_05_7_un_400_no_llama_a_ningun_proveedor()
    {
        var cfg = Principal();
        Respaldo(cfg, 1, "r1.test", LlaveR1, "modelo-r1", "3", "6");
        var h = new PorHost().RutaOk("principal.test").RutaOk("r1.test");
        using var e = new Entorno(h, cfg);

        var r = await e.Cliente.PostAsync("/api/asistente/preguntar", new StringContent(
            "{\"pregunta\":\"\",\"usuario\":{\"rol\":\"estudiante\",\"id\":\"T-E1\"}}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(0, h.Total);
        var l = await e.UnaLinea();
        Assert.Equal(400, Codigo(l));
        Assert.Null(Proveedor(l));
    }
}

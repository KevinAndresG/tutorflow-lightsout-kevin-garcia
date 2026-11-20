using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TutorFlow.Tests;

public sealed record PeticionRegistrada(string Metodo, string Url, IReadOnlyDictionary<string, string> Cabeceras, string Cuerpo)
{
    public JsonNode Json => JsonNode.Parse(Cuerpo)!;
    public string? Cabecera(string nombre) => Cabeceras.TryGetValue(nombre, out var v) ? v : null;
}

/// <summary>Manejador HTTP falso del cliente "llm": registra cada petición y devuelve respuestas guionadas. Nunca usa red.</summary>
public sealed class ProveedorHttpFalso : HttpMessageHandler
{
    private readonly Queue<Func<PeticionRegistrada, HttpResponseMessage>> _pasos = new();
    public List<PeticionRegistrada> Peticiones { get; } = [];
    public int Cuenta { get { lock (Peticiones) return Peticiones.Count; } }

    public ProveedorHttpFalso Responde(HttpStatusCode codigo, string cuerpo, string tipo = "application/json")
    {
        _pasos.Enqueue(_ => new HttpResponseMessage(codigo) { Content = new StringContent(cuerpo, Encoding.UTF8, tipo) });
        return this;
    }

    public ProveedorHttpFalso Ok(string cuerpo) => Responde(HttpStatusCode.OK, cuerpo);

    public ProveedorHttpFalso Lanza(Exception e) { _pasos.Enqueue(_ => throw e); return this; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var cab = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers) cab[h.Key] = string.Join(",", h.Value);
        string cuerpo = "";
        if (request.Content != null)
        {
            foreach (var h in request.Content.Headers) cab[h.Key] = string.Join(",", h.Value);
            cuerpo = await request.Content.ReadAsStringAsync(ct);
        }
        var reg = new PeticionRegistrada(request.Method.Method, request.RequestUri!.ToString(), cab, cuerpo);
        Func<PeticionRegistrada, HttpResponseMessage> paso;
        lock (Peticiones)
        {
            Peticiones.Add(reg);
            if (_pasos.Count == 0) throw new InvalidOperationException("Guion del proveedor falso agotado.");
            paso = _pasos.Dequeue();
        }
        return paso(reg);
    }
}

public sealed class LogsEnMemoria : ILoggerProvider
{
    private readonly List<string> _lineas = [];
    public string Todo { get { lock (_lineas) return string.Join("\n", _lineas); } }

    public ILogger CreateLogger(string categoria) => new Registro(this, categoria);
    public void Dispose() { }

    private void Agregar(string s) { lock (_lineas) _lineas.Add(s); }

    private sealed class Registro(LogsEnMemoria dueno, string categoria) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel nivel, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> f)
        {
            var sb = new StringBuilder().Append(categoria).Append(' ').Append(f(state, ex));
            if (state is IEnumerable<KeyValuePair<string, object?>> kv)
                foreach (var p in kv) sb.Append(' ').Append(p.Key).Append('=').Append(p.Value);
            if (ex != null) sb.Append(' ').Append(ex);
            dueno.Agregar(sb.ToString());
        }
    }
}

/// <summary>API con los adaptadores reales de LLM y el proveedor HTTP falso. Captura los logs de la aplicación.</summary>
public sealed class EntornoProveedor : IDisposable
{
    public const string Llave = "sk-SECRETA-9f3c1a77-no-debe-aparecer";
    public const string MarcaProveedor = "CUERPO-INTERNO-DEL-PROVEEDOR-7731";
    public const string Url = "/api/asistente/preguntar";

    private readonly WebApplicationFactory<Program> _fabrica;
    public ProveedorHttpFalso Proveedor { get; } = new();
    public LogsEnMemoria Logs { get; } = new();
    public HttpClient Cliente { get; }

    public EntornoProveedor(string? proveedor, string? llave = Llave, string? baseUrl = null, string? modelo = null)
    {
        var cfg = new Dictionary<string, string?> { ["Datos:Ruta"] = Fabrica.RutaFixtures, ["LLM_API_KEY"] = llave ?? "" };
        if (proveedor != null) cfg["LLM_PROVIDER"] = proveedor;
        if (baseUrl != null) cfg["LLM_BASE_URL"] = baseUrl;
        if (modelo != null) cfg["LLM_MODEL"] = modelo;

        _fabrica = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            foreach (var (k, v) in cfg) b.UseSetting(k, v);
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(cfg));
            b.ConfigureServices(sv => sv.AddLogging(l => { l.SetMinimumLevel(LogLevel.Trace); l.AddProvider(Logs); }));
            b.ConfigureTestServices(sv => sv.AddHttpClient("llm").ConfigurePrimaryHttpMessageHandler(() => Proveedor));
        });
        // CreateClient arranca el host: un valor inválido de LLM_PROVIDER lanza aquí.
        Cliente = _fabrica.CreateClient();
        Cliente.Timeout = TimeSpan.FromSeconds(60);
    }

    public async Task<(HttpStatusCode Codigo, string Cuerpo)> Preguntar(string pregunta, string rol = "estudiante", string id = "T-E1")
    {
        var json = JsonSerializer.Serialize(new { pregunta, usuario = new { rol, id } });
        var r = await Cliente.PostAsync(Url, new StringContent(json, Encoding.UTF8, "application/json"));
        return (r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    public void Dispose() { Cliente.Dispose(); _fabrica.Dispose(); }
}

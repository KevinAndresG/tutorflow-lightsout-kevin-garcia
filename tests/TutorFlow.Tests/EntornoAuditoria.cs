using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TutorFlow.Api.Llm;

namespace TutorFlow.Tests;

/// <summary>Modelo falso sin estado, seguro para peticiones concurrentes y con comportamiento arbitrario.</summary>
public sealed class LlmFuncion(Func<IReadOnlyList<LlmMensaje>, CancellationToken, Task<LlmRespuesta>> f) : ILlmClient
{
    private int _llamadas;
    public int Llamadas => Volatile.Read(ref _llamadas);

    public Task<LlmRespuesta> CompletarAsync(string sistema, IReadOnlyList<LlmMensaje> mensajes,
        IReadOnlyList<LlmHerramienta> herramientas, CancellationToken ct)
    {
        Interlocked.Increment(ref _llamadas);
        return f(mensajes, ct);
    }
}

/// <summary>
/// API con archivo de auditoría temporal único (Auditoria:Ruta), logs en memoria y, o bien un modelo falso (ILlmClient),
/// o bien los adaptadores reales con proveedor HTTP falso. Nunca usa red ni el logs/ del repositorio.
/// </summary>
public sealed class EntornoAuditoria : IDisposable
{
    public const string Llave = "sk-AUDITORIA-llave-secreta-5e1b9c42";
    public const string Url = "/api/asistente/preguntar";

    private readonly WebApplicationFactory<Program> _fabrica;
    public string RutaAuditoria { get; }
    public HttpClient Cliente { get; }
    public LogsEnMemoria Logs { get; } = new();
    public ProveedorHttpFalso Proveedor { get; } = new();

    /// <param name="llm">Modelo falso; si es null se usan los adaptadores reales con <see cref="Proveedor"/>.</param>
    /// <param name="config">Claves extra de configuración (precios, plazo...).</param>
    /// <param name="llave">Valor de LLM_API_KEY ("" = sin llave).</param>
    /// <param name="rutaAuditoria">Ruta de auditoría; por defecto un archivo temporal único.</param>
    public EntornoAuditoria(ILlmClient? llm, IDictionary<string, string?>? config = null, string llave = Llave,
        string? rutaAuditoria = null)
    {
        RutaAuditoria = rutaAuditoria ?? Path.Combine(Path.GetTempPath(), "tf-audit-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var cfg = new Dictionary<string, string?>
        {
            ["Datos:Ruta"] = Fabrica.RutaFixtures,
            ["LLM_API_KEY"] = llave,
            ["Auditoria:Ruta"] = RutaAuditoria,
        };
        if (config != null) foreach (var (k, v) in config) cfg[k] = v;

        _fabrica = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            foreach (var (k, v) in cfg) b.UseSetting(k, v);
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(cfg));
            b.ConfigureServices(sv => sv.AddLogging(l => { l.SetMinimumLevel(LogLevel.Trace); l.AddProvider(Logs); }));
            b.ConfigureTestServices(sv =>
            {
                sv.AddHttpClient("llm").ConfigurePrimaryHttpMessageHandler(() => (HttpMessageHandler)Proveedor);
                if (llm != null)
                {
                    sv.RemoveAll<ILlmClient>();
                    sv.AddSingleton(llm);
                }
            });
        });
        // CreateClient arranca el host: una configuración inválida (p. ej. precios) lanza aquí.
        Cliente = _fabrica.CreateClient();
        Cliente.Timeout = TimeSpan.FromSeconds(60);
    }

    public async Task<(HttpStatusCode Codigo, string Cuerpo)> Preguntar(string pregunta, string rol, string id,
        CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new { pregunta, usuario = new { rol, id } });
        return await Raw(json, ct);
    }

    public async Task<(HttpStatusCode Codigo, string Cuerpo)> Raw(string json, CancellationToken ct = default)
    {
        var r = await Cliente.PostAsync(Url, new StringContent(json, Encoding.UTF8, "application/json"), ct);
        return (r.StatusCode, await r.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Texto completo del archivo de auditoría ("" si no existe).</summary>
    public string TextoAuditoria()
    {
        if (!File.Exists(RutaAuditoria)) return "";
        using var fs = new FileStream(RutaAuditoria, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    /// <summary>Líneas no vacías del archivo; espera hasta <paramref name="msMax"/> a que haya al menos <paramref name="esperadas"/>.</summary>
    public async Task<List<string>> LineasCrudas(int esperadas, int msMax = 5000)
    {
        var fin = DateTime.UtcNow.AddMilliseconds(msMax);
        List<string> lineas;
        do
        {
            lineas = TextoAuditoria().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
            if (lineas.Count >= esperadas) break;
            await Task.Delay(50);
        } while (DateTime.UtcNow < fin);
        return lineas;
    }

    /// <summary>Línea única esperada (falla si hay más o menos de una).</summary>
    public async Task<JsonElement> UnaLinea()
    {
        var l = await LineasCrudas(1);
        // Margen para detectar líneas duplicadas escritas tarde.
        await Task.Delay(100);
        l = await LineasCrudas(1, 0);
        Assert.Single(l);
        return JsonDocument.Parse(l[0]).RootElement.Clone();
    }

    public void Dispose()
    {
        Cliente.Dispose();
        _fabrica.Dispose();
        try { if (File.Exists(RutaAuditoria)) File.Delete(RutaAuditoria); } catch { }
    }
}

/// <summary>Aserciones comunes sobre una línea de auditoría.</summary>
public static class AuditoriaAsserts
{
    public static readonly string[] Campos =
        ["fechaHora", "rol", "id", "pregunta", "estudiantesConsultados", "citas", "codigo", "tokens", "costoUsd"];

    public static void AssertForma(JsonElement l)
    {
        Assert.Equal(JsonValueKind.Object, l.ValueKind);
        foreach (var c in Campos) Assert.True(l.TryGetProperty(c, out _), "falta el campo " + c);

        Assert.Equal(JsonValueKind.String, l.GetProperty("fechaHora").ValueKind);
        var f = DateTimeOffset.Parse(l.GetProperty("fechaHora").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.Zero, f.Offset);
        Assert.InRange((DateTimeOffset.UtcNow - f).TotalSeconds, -5, 120);

        foreach (var c in new[] { "rol", "id", "pregunta" })
            Assert.True(l.GetProperty(c).ValueKind is JsonValueKind.String or JsonValueKind.Null, c + " debe ser texto o null");

        var est = l.GetProperty("estudiantesConsultados");
        Assert.Equal(JsonValueKind.Array, est.ValueKind);
        var ids = est.EnumerateArray().Select(e => { Assert.Equal(JsonValueKind.String, e.ValueKind); return e.GetString()!; }).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        var citas = l.GetProperty("citas");
        Assert.Equal(JsonValueKind.Array, citas.ValueKind);
        foreach (var c in citas.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Object, c.ValueKind);
            Assert.Equal(JsonValueKind.String, c.GetProperty("documento").ValueKind);
            Assert.Equal(JsonValueKind.String, c.GetProperty("seccion").ValueKind);
        }

        Assert.Equal(JsonValueKind.Number, l.GetProperty("codigo").ValueKind);
        Assert.True(l.GetProperty("codigo").TryGetInt32(out _));
        Assert.Equal(JsonValueKind.Number, l.GetProperty("tokens").ValueKind);
        Assert.True(l.GetProperty("tokens").TryGetInt64(out var tk) && tk >= 0);
        Assert.Equal(JsonValueKind.Number, l.GetProperty("costoUsd").ValueKind);
        Assert.True(l.GetProperty("costoUsd").GetDouble() >= 0);
    }

    public static string[] Estudiantes(JsonElement l) =>
        l.GetProperty("estudiantesConsultados").EnumerateArray().Select(e => e.GetString()!).ToArray();

    public static int Codigo(JsonElement l) => l.GetProperty("codigo").GetInt32();
    public static long Tokens(JsonElement l) => l.GetProperty("tokens").GetInt64();
    public static double Costo(JsonElement l) => l.GetProperty("costoUsd").GetDouble();
    public static string? Texto(JsonElement l, string campo) =>
        l.GetProperty(campo).ValueKind == JsonValueKind.Null ? null : l.GetProperty(campo).GetString();

    /// <summary>(entrada*pE + salida*pS) / 1e6.</summary>
    public static double CostoEsperado(long entrada, long salida, double precioEntrada, double precioSalida) =>
        (entrada * precioEntrada + salida * precioSalida) / 1_000_000.0;
}

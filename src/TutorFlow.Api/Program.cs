using System.Text.Json;
using TutorFlow.Api;
using TutorFlow.Api.Llm;

var builder = WebApplication.CreateBuilder(args);

static string ResolverRuta(string? configurada)
{
    if (!string.IsNullOrWhiteSpace(configurada)) return Path.GetFullPath(configurada);
    foreach (var inicio in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        for (var d = new DirectoryInfo(inicio); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "data", "api"))) return Path.Combine(d.FullName, "data");
    return Path.GetFullPath("data");
}

var ruta = ResolverRuta(builder.Configuration["Datos:Ruta"]);
builder.Services.AddSingleton(_ => new Reglamento(Path.Combine(ruta, "reglamento")));
builder.Services.AddSingleton(_ => new Datos(Path.Combine(ruta, "api")));
builder.Services.AddHttpClient("llm", c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton<ILlmClient>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var proveedor = (cfg["LLM_PROVIDER"] ?? "").Trim();
    if (proveedor.Length == 0 || proveedor.Equals("openai", StringComparison.OrdinalIgnoreCase))
        return ActivatorUtilities.CreateInstance<OpenAiCompatClient>(sp);
    if (proveedor.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
        return ActivatorUtilities.CreateInstance<AnthropicClient>(sp);
    throw new InvalidOperationException("LLM_PROVIDER no es válido: use 'openai' o 'anthropic'.");
});
builder.Services.AddSingleton<Asistente>();

var app = builder.Build();

// Carga eager: un dato corrupto falla al arrancar, nunca convierte un 400 en 500.
app.Services.GetRequiredService<Reglamento>();
app.Services.GetRequiredService<Datos>();
app.Services.GetRequiredService<ILlmClient>();

app.MapGet("/api/salud", () => Results.Json(new { estado = "ok" }));

app.MapPost("/api/asistente/preguntar", async (HttpRequest req, Datos datos, IConfiguration cfg, Asistente asistente) =>
{
    static IResult Error(int codigo, string msg) => Results.Json(new { error = msg }, statusCode: codigo);

    JsonDocument doc;
    try { doc = await JsonDocument.ParseAsync(req.Body); }
    catch (JsonException) { return Error(400, "El cuerpo no es un JSON válido."); }

    using (doc)
    {
        var raiz = doc.RootElement;
        if (raiz.ValueKind != JsonValueKind.Object) return Error(400, "El cuerpo debe ser un objeto JSON.");

        if (!raiz.TryGetProperty("pregunta", out var p) || p.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(p.GetString()))
            return Error(400, "La pregunta es obligatoria.");
        if (p.GetString()!.Length > 1000) return Error(400, "La pregunta no puede superar 1.000 caracteres.");

        if (!raiz.TryGetProperty("usuario", out var u) || u.ValueKind != JsonValueKind.Object)
            return Error(400, "El usuario es obligatorio.");
        if (!u.TryGetProperty("rol", out var r) || r.ValueKind != JsonValueKind.String)
            return Error(400, "El rol es obligatorio.");
        var rol = r.GetString()!;
        if (rol != "estudiante" && rol != "coordinador")
            return Error(400, "El rol debe ser 'estudiante' o 'coordinador'.");
        if (!u.TryGetProperty("id", out var i) || i.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(i.GetString()))
            return Error(400, "El id del usuario es obligatorio.");

        var usuario = Permisos.ResolverUsuario(datos, rol, i.GetString()!);
        if (usuario == null) return Error(403, "Usuario no válido.");
        if (Permisos.MencionaTercero(datos, usuario, p.GetString()!))
            return Error(403, "No tienes permiso para consultar datos de otros estudiantes.");

        if (string.IsNullOrWhiteSpace(cfg["LLM_API_KEY"]))
            return Error(503, "El servicio del modelo no está disponible: falta configurar LLM_API_KEY.");

        AsistenteResultado res;
        try { res = await asistente.ResponderAsync(usuario, p.GetString()!, req.HttpContext.RequestAborted); }
        catch (OperationCanceledException) when (req.HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception) { return Error(503, "El proveedor del modelo no está disponible."); }

        if (res.PermisoDenegado) return Error(403, "No tienes permiso para consultar esos datos.");

        // T5 reemplaza este 0 por el cálculo con el precio configurado.
        const double costoUsd = 0;
        return Results.Json(new
        {
            respuesta = res.Respuesta,
            citas = res.Citas.Select(c => new { documento = c.Documento, seccion = c.Seccion }),
            usoDatosEstudiante = res.UsoDatosEstudiante,
            tokens = res.Tokens,
            costoUsd,
        });
    }
});

app.Run();

public partial class Program { }

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
builder.Services.AddSingleton<ILlmClient, SinLlmClient>();

var app = builder.Build();

// Carga eager: un dato corrupto falla al arrancar, nunca convierte un 400 en 500.
app.Services.GetRequiredService<Reglamento>();
app.Services.GetRequiredService<Datos>();

app.MapGet("/api/salud", () => Results.Json(new { estado = "ok" }));

app.MapPost("/api/asistente/preguntar", async (HttpRequest req, Datos datos, IConfiguration cfg) =>
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

        // T4: asistente real.
        return Error(503, "El asistente aún no está disponible.");
    }
});

app.Run();

public partial class Program { }

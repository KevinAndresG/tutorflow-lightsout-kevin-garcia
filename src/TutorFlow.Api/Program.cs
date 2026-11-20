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
builder.Services.AddHttpClient("llm", c => c.Timeout = TimeSpan.FromSeconds(60))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton(sp => Precios.Desde(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<Auditoria>();
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
app.Services.GetRequiredService<Precios>();

app.MapGet("/api/salud", () => Results.Json(new { estado = "ok" }));

app.MapPost("/api/asistente/preguntar", async (HttpRequest req, Datos datos, IConfiguration cfg, Asistente asistente,
    Auditoria auditoria, Precios precios, ILogger<Program> log) =>
{
    // Un solo punto de registro: el finally cubre toda ruta de salida.
    string? rolAud = null, idAud = null, preguntaAud = null;
    var codigo = 500;
    IReadOnlyList<Cita> citasAud = [];
    var medicion = new MedicionUso();

    IResult Error(int c, string msg)
    {
        codigo = c;
        return Results.Json(new { error = msg }, statusCode: c);
    }

    try
    {
        const int MaxCuerpo = 64 * 1024;
        var limite = req.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (limite is { IsReadOnly: false }) limite.MaxRequestBodySize = MaxCuerpo;

        JsonDocument doc;
        try
        {
            using var ms = new MemoryStream();
            var buf = new byte[8192];
            int n;
            while ((n = await req.Body.ReadAsync(buf, req.HttpContext.RequestAborted)) > 0)
            {
                if (ms.Length + n > MaxCuerpo) return Error(400, "El cuerpo es demasiado grande.");
                ms.Write(buf, 0, n);
            }
            ms.Position = 0;
            doc = await JsonDocument.ParseAsync(ms);
        }
        catch (JsonException) { return Error(400, "El cuerpo no es un JSON válido."); }
        catch (OperationCanceledException) when (req.HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception) { return Error(400, "No se pudo leer el cuerpo de la petición."); }

        using (doc)
        {
            var raiz = doc.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object) return Error(400, "El cuerpo debe ser un objeto JSON.");

            static string? Texto(JsonElement e, string nombre) =>
                e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nombre, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() : null;
            preguntaAud = Texto(raiz, "pregunta");
            if (raiz.TryGetProperty("usuario", out var uAud))
            {
                rolAud = Texto(uAud, "rol");
                idAud = Texto(uAud, "id");
            }

            if (string.IsNullOrWhiteSpace(preguntaAud)) return Error(400, "La pregunta es obligatoria.");
            if (preguntaAud.Length > 1000) return Error(400, "La pregunta no puede superar 1.000 caracteres.");

            if (!raiz.TryGetProperty("usuario", out var u) || u.ValueKind != JsonValueKind.Object)
                return Error(400, "El usuario es obligatorio.");
            if (rolAud == null) return Error(400, "El rol es obligatorio.");
            if (rolAud != "estudiante" && rolAud != "coordinador")
                return Error(400, "El rol debe ser 'estudiante' o 'coordinador'.");
            if (string.IsNullOrWhiteSpace(idAud)) return Error(400, "El id del usuario es obligatorio.");

            var usuario = Permisos.ResolverUsuario(datos, rolAud, idAud);
            if (usuario == null) return Error(403, "Usuario no válido.");
            if (Permisos.MencionaTercero(datos, usuario, preguntaAud))
                return Error(403, "No tienes permiso para consultar datos de otros estudiantes.");

            if (string.IsNullOrWhiteSpace(cfg["LLM_API_KEY"]))
                return Error(503, "El servicio del modelo no está disponible: falta configurar LLM_API_KEY.");

            var plazo = double.TryParse(cfg["Asistente:PlazoSegundos"], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seg) && seg > 0 ? seg : 90;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(req.HttpContext.RequestAborted);
            cts.CancelAfter(TimeSpan.FromSeconds(plazo));

            AsistenteResultado res;
            try { res = await asistente.ResponderAsync(usuario, preguntaAud, cts.Token, medicion); }
            catch (OperationCanceledException) when (req.HttpContext.RequestAborted.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                log.LogWarning("Falla del asistente o del proveedor ({Tipo}).", e.GetType().Name);
                return Error(503, "El proveedor del modelo no está disponible.");
            }

            if (res.PermisoDenegado) return Error(403, "No tienes permiso para consultar esos datos.");

            codigo = 200;
            citasAud = res.Citas;
            return Results.Json(new
            {
                respuesta = res.Respuesta,
                citas = res.Citas.Select(c => new { documento = c.Documento, seccion = c.Seccion }),
                usoDatosEstudiante = res.UsoDatosEstudiante,
                tokens = medicion.Total,
                costoUsd = precios.Costo(medicion.TokensEntrada, medicion.TokensSalida),
            });
        }
    }
    catch (OperationCanceledException) when (req.HttpContext.RequestAborted.IsCancellationRequested)
    {
        codigo = 499;
        throw;
    }
    catch (Exception e)
    {
        log.LogError("Excepción inesperada ({Tipo}).", e.GetType().Name);
        return Error(503, "El servicio no está disponible.");
    }
    finally
    {
        auditoria.Registrar(new RegistroAuditoria(rolAud, idAud, preguntaAud, medicion.Ids.ToList(), citasAud, codigo,
            medicion.Total, precios.Costo(medicion.TokensEntrada, medicion.TokensSalida)));
    }
});

app.Run();

public partial class Program { }

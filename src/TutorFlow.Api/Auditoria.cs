using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TutorFlow.Api;

// Precio USD por millón de tokens. Ausente = 0; un valor no numérico o negativo impide arrancar.
public record Precios(double EntradaMtok, double SalidaMtok)
{
    public static Precios Desde(IConfiguration cfg) =>
        new(Leer(cfg, "LLM_PRECIO_ENTRADA_MTOK"), Leer(cfg, "LLM_PRECIO_SALIDA_MTOK"));

    static double Leer(IConfiguration cfg, string nombre)
    {
        var s = cfg[nombre]?.Trim();
        if (string.IsNullOrEmpty(s)) return 0;
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v) || v < 0)
            throw new InvalidOperationException($"{nombre} debe ser un número no negativo (USD por millón de tokens, con punto decimal).");
        return v;
    }

    public double Costo(int tokensEntrada, int tokensSalida) =>
        (tokensEntrada * EntradaMtok + tokensSalida * SalidaMtok) / 1_000_000d;
}

public record RegistroAuditoria(
    string? Rol, string? Id, string? Pregunta,
    IReadOnlyList<string> EstudiantesConsultados, IReadOnlyList<Cita> Citas,
    int Codigo, int Tokens, double CostoUsd);

// Una línea JSON por pregunta. Un fallo al escribir nunca tumba la petición.
public class Auditoria(IConfiguration cfg, ILogger<Auditoria> logger)
{
    const int MaxPregunta = 10_000;
    static readonly JsonSerializerOptions Opciones = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    readonly object candado = new();
    readonly string ruta = Path.GetFullPath(
        string.IsNullOrWhiteSpace(cfg["Auditoria:Ruta"]) ? Path.Combine("logs", "auditoria.jsonl") : cfg["Auditoria:Ruta"]!);

    public void Registrar(RegistroAuditoria r)
    {
        string linea;
        try
        {
            var llave = cfg["LLM_API_KEY"]?.Trim();
            string? Limpiar(string? s) =>
                s != null && !string.IsNullOrEmpty(llave) ? s.Replace(llave, "[REDACTADO]", StringComparison.Ordinal) : s;

            var pregunta = Limpiar(r.Pregunta);
            if (pregunta is { Length: > MaxPregunta }) pregunta = pregunta[..MaxPregunta];
            linea = JsonSerializer.Serialize(new
            {
                fechaHora = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                rol = Limpiar(r.Rol),
                id = Limpiar(r.Id),
                pregunta,
                estudiantesConsultados = r.EstudiantesConsultados.Select(i => Limpiar(i)).ToList(),
                citas = r.Citas.Select(c => new { documento = c.Documento, seccion = c.Seccion }).ToList(),
                codigo = r.Codigo,
                tokens = r.Tokens,
                costoUsd = r.CostoUsd,
            }, Opciones);
        }
        catch (Exception e)
        {
            logger.LogError("No se pudo construir la línea de auditoría ({Tipo}).", e.GetType().Name);
            return;
        }

        logger.LogInformation("Auditoria {Linea}", linea);
        try
        {
            lock (candado)
            {
                var dir = Path.GetDirectoryName(ruta);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(ruta, linea + "\n");
            }
        }
        catch (Exception e)
        {
            logger.LogError("No se pudo escribir la auditoría ({Tipo}).", e.GetType().Name);
        }
    }
}

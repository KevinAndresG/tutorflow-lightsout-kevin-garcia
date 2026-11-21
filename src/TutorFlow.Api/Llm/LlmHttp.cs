using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TutorFlow.Api.Llm;

// Falla del proveedor. El mensaje nunca incluye llaves, cuerpos ni cabeceras.
public class LlmException(string mensaje, LlmCausa causa = LlmCausa.Otro, int? estadoHttp = null, string? codigoProveedor = null) : Exception(mensaje)
{
    public LlmCausa Causa { get; } = causa;
    // Solo para diagnóstico en logs: código HTTP y error.code/error.type del proveedor (saneado), nunca el mensaje libre.
    public int? EstadoHttp { get; } = estadoHttp;
    public string? CodigoProveedor { get; } = codigoProveedor;
}

// Solo las causas que justifican saltar al siguiente proveedor tienen valor propio.
public enum LlmCausa { Otro, Http429, Http400, Http503, Tiempo }

static class LlmHttp
{
    public static async Task<JsonDocument> EnviarAsync(HttpClient http, HttpRequestMessage req, JsonNode cuerpo, CancellationToken ct)
    {
        req.Content = new StringContent(cuerpo.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var codigo = await LeerCodigoErrorAsync(resp, ct);
                throw new LlmException($"El proveedor del modelo respondió {(int)resp.StatusCode}.",
                    (int)resp.StatusCode switch { 429 => LlmCausa.Http429, 400 => LlmCausa.Http400, 503 => LlmCausa.Http503, _ => LlmCausa.Otro },
                    (int)resp.StatusCode, codigo);
            }
            var texto = await resp.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(texto);
        }
        catch (LlmException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new LlmException("El proveedor del modelo no respondió a tiempo.", LlmCausa.Tiempo); }
        catch (JsonException) { throw new LlmException("El proveedor del modelo devolvió una respuesta no válida."); }
        catch (HttpRequestException) { throw new LlmException("No se pudo contactar al proveedor del modelo."); }
        // Cualquier otra excepción (p. ej. FormatException por cabeceras inválidas) puede llevar la llave en su mensaje.
        catch (Exception) { throw new LlmException("No se pudo contactar al proveedor del modelo."); }
    }

    // Extrae error.code (o error.type) del cuerpo de error. Devuelve null si no hay o si el cuerpo no es JSON.
    // Es best-effort: cualquier fallo de lectura se ignora para no ocultar el estado HTTP.
    static async Task<string?> LeerCodigoErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var texto = await resp.Content.ReadAsStringAsync(ct);
            if (texto.Length > 64 * 1024) return null;
            using var d = JsonDocument.Parse(texto);
            if (!d.RootElement.TryGetProperty("error", out var err) || err.ValueKind != JsonValueKind.Object) return null;
            foreach (var campo in new[] { "code", "type" })
                if (err.TryGetProperty(campo, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var limpio = SanearCodigo(v.GetString());
                    if (limpio is not null) return limpio;
                }
            return null;
        }
        catch (Exception e) when (e is JsonException or HttpRequestException or InvalidOperationException) { return null; }
    }

    // Solo [A-Za-z0-9_.-], máximo 64 caracteres. Cualquier otro carácter se descarta.
    public static string? SanearCodigo(string? valor)
    {
        if (string.IsNullOrEmpty(valor)) return null;
        var sb = new StringBuilder();
        foreach (var ch in valor)
        {
            if (sb.Length >= 64) break;
            if (ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '.' or '-') sb.Append(ch);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    public static JsonNode ParseOVacio(string json)
    {
        try { return JsonNode.Parse(json) ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    public static int Entero(JsonElement padre, string nombre) =>
        padre.ValueKind == JsonValueKind.Object && padre.TryGetProperty(nombre, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? Math.Max(0, n) : 0;
}

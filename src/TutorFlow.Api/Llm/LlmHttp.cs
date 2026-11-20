using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TutorFlow.Api.Llm;

// Falla del proveedor. El mensaje nunca incluye llaves, cuerpos ni cabeceras.
public class LlmException(string mensaje) : Exception(mensaje);

static class LlmHttp
{
    public static async Task<JsonDocument> EnviarAsync(HttpClient http, HttpRequestMessage req, JsonNode cuerpo, CancellationToken ct)
    {
        req.Content = new StringContent(cuerpo.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                throw new LlmException($"El proveedor del modelo respondió {(int)resp.StatusCode}.");
            var texto = await resp.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(texto);
        }
        catch (LlmException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new LlmException("El proveedor del modelo no respondió a tiempo."); }
        catch (JsonException) { throw new LlmException("El proveedor del modelo devolvió una respuesta no válida."); }
        catch (HttpRequestException) { throw new LlmException("No se pudo contactar al proveedor del modelo."); }
        // Cualquier otra excepción (p. ej. FormatException por cabeceras inválidas) puede llevar la llave en su mensaje.
        catch (Exception) { throw new LlmException("No se pudo contactar al proveedor del modelo."); }
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

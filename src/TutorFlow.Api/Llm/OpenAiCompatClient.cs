using System.Text.Json;
using System.Text.Json.Nodes;

namespace TutorFlow.Api.Llm;

public class OpenAiCompatClient(IHttpClientFactory fabrica, IConfiguration cfg, string prefijo = "LLM_") : ILlmClient
{
    public const string BaseDefecto = "https://generativelanguage.googleapis.com/v1beta/openai";
    // gemini-2.5-flash ya no está disponible para cuentas nuevas; gemini-3.5-flash comprobado con la API real.
    public const string ModeloDefecto = "gemini-3.5-flash";
    // Los modelos Gemini 3 gastan tokens de razonamiento antes de responder; con 2048 puede quedar sin texto visible.
    const int MaxTokens = 8192;

    public string? ModeloEfectivo() => string.IsNullOrWhiteSpace(cfg[prefijo + "MODEL"]) ? ModeloDefecto : cfg[prefijo + "MODEL"]!.Trim();

    public async Task<LlmRespuesta> CompletarAsync(string sistema, IReadOnlyList<LlmMensaje> mensajes,
        IReadOnlyList<LlmHerramienta> herramientas, CancellationToken ct)
    {
        var baseUrl = (string.IsNullOrWhiteSpace(cfg[prefijo + "BASE_URL"]) ? BaseDefecto : cfg[prefijo + "BASE_URL"]!).TrimEnd('/');
        var modelo = ModeloEfectivo()!;

        var msgs = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = sistema } };
        foreach (var m in mensajes)
        {
            if (m.Rol == "tool")
                msgs.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = m.ToolCallId, ["content"] = m.Contenido });
            else if (m.Rol == "assistant" && m.Llamadas is { Count: > 0 })
            {
                var calls = new JsonArray();
                foreach (var l in m.Llamadas)
                {
                    var call = new JsonObject
                    {
                        ["id"] = l.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = l.Nombre, ["arguments"] = l.ArgumentosJson },
                    };
                    // Gemini exige reenviar extra_content (thought_signature) tal cual lo emitió.
                    if (!string.IsNullOrEmpty(l.ExtraJson))
                        call["extra_content"] = JsonNode.Parse(l.ExtraJson);
                    calls.Add(call);
                }
                msgs.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = string.IsNullOrEmpty(m.Contenido) ? null : m.Contenido,
                    ["tool_calls"] = calls,
                });
            }
            else
                msgs.Add(new JsonObject { ["role"] = m.Rol, ["content"] = m.Contenido });
        }

        var cuerpo = new JsonObject { ["model"] = modelo, ["messages"] = msgs, ["max_tokens"] = MaxTokens };
        if (herramientas.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var h in herramientas)
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = h.Nombre,
                        ["description"] = h.Descripcion,
                        ["parameters"] = LlmHttp.ParseOVacio(h.ParametrosJsonSchema),
                    },
                });
            cuerpo["tools"] = tools;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cfg[prefijo + "API_KEY"]?.Trim());
        using var doc = await LlmHttp.EnviarAsync(fabrica.CreateClient("llm"), req, cuerpo, ct);

        try
        {
            var raiz = doc.RootElement;
            var mensaje = raiz.GetProperty("choices")[0].GetProperty("message");
            string? texto = mensaje.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var llamadas = new List<LlmLlamadaHerramienta>();
            if (mensaje.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
            {
                var n = 0;
                foreach (var t in tc.EnumerateArray())
                {
                    var f = t.GetProperty("function");
                    var id = t.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(i.GetString())
                        ? i.GetString()! : $"call_{n}";
                    var args = f.TryGetProperty("arguments", out var a)
                        ? (a.ValueKind == JsonValueKind.String ? a.GetString()! : a.GetRawText()) : "{}";
                    // Solo un objeto JSON se guarda y reenvía; otro tipo se descarta. Tampoco se guarda uno de más de 8 KB:
                    // se reenvía en cada vuelta, así que un proveedor hostil podría inflar tokens y memoria.
                    var extra = t.TryGetProperty("extra_content", out var ex) && ex.ValueKind == JsonValueKind.Object
                        ? ex.GetRawText() : null;
                    if (extra is not null && extra.Length > 8 * 1024) extra = null;
                    llamadas.Add(new(id, f.GetProperty("name").GetString()!, string.IsNullOrWhiteSpace(args) ? "{}" : args, extra));
                    n++;
                }
            }
            var uso = raiz.TryGetProperty("usage", out var u) ? u : default;
            var entrada = LlmHttp.Entero(uso, "prompt_tokens");
            var completion = LlmHttp.Entero(uso, "completion_tokens");
            // Gemini 3 reporta los tokens de razonamiento solo en total_tokens (completion_tokens no los incluye)
            // y los cobra como salida. Por eso la salida es max(completion, total - prompt); nunca baja de completion
            // y no se suma reasoning_tokens aparte para no contarlos dos veces. Si total falta o es inválido, vale 0.
            var restante = (long)LlmHttp.Entero(uso, "total_tokens") - entrada;
            var salida = (int)Math.Max(completion, Math.Min(restante, int.MaxValue));
            return new(texto, llamadas, new(entrada, salida),
                raiz.TryGetProperty("model", out var mo) && mo.ValueKind == JsonValueKind.String ? mo.GetString() : null);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new LlmException("El proveedor del modelo devolvió una respuesta sin los campos esperados.");
        }
    }
}

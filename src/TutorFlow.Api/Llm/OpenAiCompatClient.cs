using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TutorFlow.Api.Llm;

public class OpenAiCompatClient(IHttpClientFactory fabrica, IConfiguration cfg) : ILlmClient
{
    public const string BaseDefecto = "https://generativelanguage.googleapis.com/v1beta/openai";
    public const string ModeloDefecto = "gemini-2.5-flash";

    public async Task<LlmRespuesta> CompletarAsync(string sistema, IReadOnlyList<LlmMensaje> mensajes,
        IReadOnlyList<LlmHerramienta> herramientas, CancellationToken ct)
    {
        var baseUrl = (string.IsNullOrWhiteSpace(cfg["LLM_BASE_URL"]) ? BaseDefecto : cfg["LLM_BASE_URL"]!).TrimEnd('/');
        var modelo = string.IsNullOrWhiteSpace(cfg["LLM_MODEL"]) ? ModeloDefecto : cfg["LLM_MODEL"]!;

        var msgs = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = sistema } };
        foreach (var m in mensajes)
        {
            if (m.Rol == "tool")
                msgs.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = m.ToolCallId, ["content"] = m.Contenido });
            else if (m.Rol == "assistant" && m.Llamadas is { Count: > 0 })
            {
                var calls = new JsonArray();
                foreach (var l in m.Llamadas)
                    calls.Add(new JsonObject
                    {
                        ["id"] = l.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = l.Nombre, ["arguments"] = l.ArgumentosJson },
                    });
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

        var cuerpo = new JsonObject { ["model"] = modelo, ["messages"] = msgs };
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
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg["LLM_API_KEY"]);
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
                    llamadas.Add(new(id, f.GetProperty("name").GetString()!, string.IsNullOrWhiteSpace(args) ? "{}" : args));
                    n++;
                }
            }
            var uso = raiz.TryGetProperty("usage", out var u) ? u : default;
            return new(texto, llamadas, new(LlmHttp.Entero(uso, "prompt_tokens"), LlmHttp.Entero(uso, "completion_tokens")));
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new LlmException("El proveedor del modelo devolvió una respuesta sin los campos esperados.");
        }
    }
}

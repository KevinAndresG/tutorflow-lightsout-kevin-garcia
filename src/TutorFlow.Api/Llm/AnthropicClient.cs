using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TutorFlow.Api.Llm;

public class AnthropicClient(IHttpClientFactory fabrica, IConfiguration cfg) : ILlmClient
{
    public const string BaseDefecto = "https://api.anthropic.com";
    public const string ModeloDefecto = "claude-haiku-4-5";
    const int MaxTokens = 2048;

    public async Task<LlmRespuesta> CompletarAsync(string sistema, IReadOnlyList<LlmMensaje> mensajes,
        IReadOnlyList<LlmHerramienta> herramientas, CancellationToken ct)
    {
        var baseUrl = (string.IsNullOrWhiteSpace(cfg["LLM_BASE_URL"]) ? BaseDefecto : cfg["LLM_BASE_URL"]!).TrimEnd('/');
        var modelo = string.IsNullOrWhiteSpace(cfg["LLM_MODEL"]) ? ModeloDefecto : cfg["LLM_MODEL"]!;

        var msgs = new JsonArray();
        JsonArray? resultados = null; // bloques tool_result consecutivos
        foreach (var m in mensajes)
        {
            if (m.Rol == "tool")
            {
                if (resultados == null)
                {
                    resultados = [];
                    msgs.Add(new JsonObject { ["role"] = "user", ["content"] = resultados });
                }
                resultados.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = m.ToolCallId, ["content"] = m.Contenido });
                continue;
            }
            resultados = null;
            var bloques = new JsonArray();
            if (!string.IsNullOrEmpty(m.Contenido) || m.Llamadas is not { Count: > 0 })
                bloques.Add(new JsonObject { ["type"] = "text", ["text"] = m.Contenido });
            if (m.Rol == "assistant" && m.Llamadas != null)
                foreach (var l in m.Llamadas)
                    bloques.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = l.Id,
                        ["name"] = l.Nombre,
                        ["input"] = LlmHttp.ParseOVacio(l.ArgumentosJson),
                    });
            msgs.Add(new JsonObject { ["role"] = m.Rol == "assistant" ? "assistant" : "user", ["content"] = bloques });
        }

        var cuerpo = new JsonObject
        {
            ["model"] = modelo,
            ["max_tokens"] = MaxTokens,
            ["system"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = sistema,
                    ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
                },
            },
            ["messages"] = msgs,
        };
        if (herramientas.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var h in herramientas)
                tools.Add(new JsonObject
                {
                    ["name"] = h.Nombre,
                    ["description"] = h.Descripcion,
                    ["input_schema"] = LlmHttp.ParseOVacio(h.ParametrosJsonSchema),
                });
            cuerpo["tools"] = tools;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages");
        req.Headers.TryAddWithoutValidation("x-api-key", cfg["LLM_API_KEY"]?.Trim());
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        using var doc = await LlmHttp.EnviarAsync(fabrica.CreateClient("llm"), req, cuerpo, ct);

        try
        {
            var raiz = doc.RootElement;
            var texto = new StringBuilder();
            var llamadas = new List<LlmLlamadaHerramienta>();
            foreach (var b in raiz.GetProperty("content").EnumerateArray())
            {
                var tipo = b.GetProperty("type").GetString();
                if (tipo == "text" && b.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    texto.Append(t.GetString());
                else if (tipo == "tool_use")
                    llamadas.Add(new(b.GetProperty("id").GetString()!, b.GetProperty("name").GetString()!,
                        b.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}"));
            }
            var uso = raiz.TryGetProperty("usage", out var u) ? u : default;
            var entrada = LlmHttp.Entero(uso, "input_tokens") + LlmHttp.Entero(uso, "cache_creation_input_tokens")
                          + LlmHttp.Entero(uso, "cache_read_input_tokens");
            return new(texto.Length > 0 ? texto.ToString() : null, llamadas, new(entrada, LlmHttp.Entero(uso, "output_tokens")));
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException)
        {
            throw new LlmException("El proveedor del modelo devolvió una respuesta sin los campos esperados.");
        }
    }
}

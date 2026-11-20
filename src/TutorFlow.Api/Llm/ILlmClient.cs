namespace TutorFlow.Api.Llm;

public record LlmHerramienta(string Nombre, string Descripcion, string ParametrosJsonSchema);
public record LlmLlamadaHerramienta(string Id, string Nombre, string ArgumentosJson);
// Roles: "user", "assistant" (puede traer Llamadas) y "tool" (resultado de una llamada, con ToolCallId).
public record LlmMensaje(string Rol, string Contenido, string? ToolCallId = null, IReadOnlyList<LlmLlamadaHerramienta>? Llamadas = null);
public record LlmUso(int TokensEntrada, int TokensSalida);
public record LlmRespuesta(string? Texto, IReadOnlyList<LlmLlamadaHerramienta> Llamadas, LlmUso Uso);

public interface ILlmClient
{
    Task<LlmRespuesta> CompletarAsync(string sistema, IReadOnlyList<LlmMensaje> mensajes,
        IReadOnlyList<LlmHerramienta> herramientas, CancellationToken ct);
}

namespace TutorFlow.Api.Llm;

public record LlmMensaje(string Rol, string Contenido, string? ToolCallId = null);
public record LlmHerramienta(string Nombre, string Descripcion, string ParametrosJsonSchema);
public record LlmLlamadaHerramienta(string Id, string Nombre, string ArgumentosJson);
public record LlmUso(int TokensEntrada, int TokensSalida);
public record LlmRespuesta(string? Texto, IReadOnlyList<LlmLlamadaHerramienta> Llamadas, LlmUso Uso);

public interface ILlmClient
{
    Task<LlmRespuesta> CompletarAsync(string sistema, IReadOnlyList<LlmMensaje> mensajes,
        IReadOnlyList<LlmHerramienta> herramientas, CancellationToken ct);
}

// Marcador hasta T4: todavía no hay adaptador real.
public class SinLlmClient : ILlmClient
{
    public Task<LlmRespuesta> CompletarAsync(string sistema, IReadOnlyList<LlmMensaje> mensajes,
        IReadOnlyList<LlmHerramienta> herramientas, CancellationToken ct) =>
        throw new InvalidOperationException("Cliente LLM no implementado.");
}

using TutorFlow.Api.Llm;

namespace TutorFlow.Tests;

public record InvocacionLlm(string Sistema, IReadOnlyList<LlmMensaje> Mensajes, IReadOnlyList<LlmHerramienta> Herramientas);

/// <summary>
/// Modelo falso guionado: cada paso recibe los mensajes de la vuelta y devuelve la respuesta. Registra cada invocación.
/// Si el guion se agota lanza (el asistente debe devolver 503), salvo que se use RepetirUltimo.
/// </summary>
public class FakeLlmClient : ILlmClient
{
    private readonly Queue<Func<IReadOnlyList<LlmMensaje>, LlmRespuesta>> _pasos = new();
    private Func<IReadOnlyList<LlmMensaje>, LlmRespuesta>? _ultimo;
    public bool RepetirUltimo { get; set; }
    public List<InvocacionLlm> Invocaciones { get; } = [];
    public int Llamadas => Invocaciones.Count;

    public FakeLlmClient Paso(Func<IReadOnlyList<LlmMensaje>, LlmRespuesta> paso) { _pasos.Enqueue(paso); return this; }

    public FakeLlmClient Herramienta(string nombre, string argumentosJson, int entrada = 0, int salida = 0) =>
        Paso(_ => new LlmRespuesta(null,
            [new LlmLlamadaHerramienta("call-" + Guid.NewGuid().ToString("N")[..8], nombre, argumentosJson)],
            new LlmUso(entrada, salida)));

    public FakeLlmClient Texto(string texto, int entrada = 0, int salida = 0) =>
        Paso(_ => new LlmRespuesta(texto, [], new LlmUso(entrada, salida)));

    public FakeLlmClient Final(string respuesta, string citasJson = "[]", int entrada = 0, int salida = 0) =>
        Texto("{\"respuesta\":" + System.Text.Json.JsonSerializer.Serialize(respuesta) + ",\"citas\":" + citasJson + "}",
            entrada, salida);

    public FakeLlmClient Lanza(Exception e) => Paso(_ => throw e);

    public Task<LlmRespuesta> CompletarAsync(string sistema, IReadOnlyList<LlmMensaje> mensajes,
        IReadOnlyList<LlmHerramienta> herramientas, CancellationToken ct)
    {
        lock (Invocaciones) Invocaciones.Add(new InvocacionLlm(sistema, mensajes.ToList(), herramientas.ToList()));
        Func<IReadOnlyList<LlmMensaje>, LlmRespuesta> paso;
        if (_pasos.Count > 0) { paso = _pasos.Dequeue(); _ultimo = paso; }
        else if (RepetirUltimo && _ultimo != null) paso = _ultimo;
        else throw new InvalidOperationException("Guion del modelo falso agotado.");
        return Task.FromResult(paso(mensajes));
    }

    /// <summary>Contenido concatenado de todos los mensajes "tool" que recibió el modelo en cualquier vuelta.</summary>
    public string ResultadosHerramienta() =>
        string.Join("\n", Invocaciones.SelectMany(i => i.Mensajes).Where(m => m.Rol == "tool").Select(m => m.Contenido));

    /// <summary>Todo lo que el modelo vio (sistema + mensajes) en todas las vueltas.</summary>
    public string TodoLoVisto() =>
        string.Join("\n", Invocaciones.Select(i => i.Sistema + "\n" + string.Join("\n", i.Mensajes.Select(m => m.Contenido))));
}

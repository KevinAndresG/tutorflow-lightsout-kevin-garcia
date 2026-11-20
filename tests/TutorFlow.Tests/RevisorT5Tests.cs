using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace TutorFlow.Tests;

/// <summary>Igual que BloqueaRedHandler (falla fuerte ante la red real) pero deja constancia de cada intento.</summary>
public sealed class BloqueaRedRegistrador : HttpMessageHandler
{
    public List<(string Metodo, string Url)> Intentos { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Intentos) Intentos.Add((request.Method.Method, request.RequestUri!.ToString()));
        throw new InvalidOperationException("PRUEBA INTENTÓ USAR LA RED REAL: " + request.Method + " " + request.RequestUri);
    }
}

/// <summary>Debilidades señaladas por el revisor: defensa de red no vacua, LLM_PROVIDER desconocido y caracteres Tag.</summary>
public class RevisorT5Tests
{
    private const string UrlPorDefecto = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";

    [Fact]
    public async Task T5_ESC_05_5_el_bloqueador_de_red_recibio_y_bloqueo_una_peticion_real_hacia_la_url_por_defecto()
    {
        var registrador = new BloqueaRedRegistrador();
        using var f = Fabrica.Crear("llave-falsa-t5-xyz", sv =>
            sv.AddHttpClient("llm").ConfigurePrimaryHttpMessageHandler(() => registrador));
        using var cliente = f.CreateClient();

        var r = await cliente.PostAsync(EntornoProveedor.Url, new StringContent(
            "{\"pregunta\":\"¿Nota mínima?\",\"usuario\":{\"rol\":\"estudiante\",\"id\":\"T-E1\"}}", Encoding.UTF8, "application/json"));
        var cuerpo = await r.Content.ReadAsStringAsync();

        // Prueba de que el intento existió (no es un 503 por otra causa) y fue exactamente a la URL por defecto.
        var intento = Assert.Single(registrador.Intentos);
        Assert.Equal("POST", intento.Metodo);
        Assert.Equal(UrlPorDefecto, intento.Url);

        // El 503 es el genérico: ni el mensaje de la excepción del bloqueador, ni la URL, ni la llave.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.DoesNotContain("PRUEBA INTENTÓ", cuerpo);
        Assert.DoesNotContain("generativelanguage", cuerpo);
        Assert.DoesNotContain("llave-falsa-t5-xyz", cuerpo);
        Assert.Contains("\"error\"", cuerpo);
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("azure")]
    [InlineData("claude")]
    public void T5_ESC_05_5_LLM_PROVIDER_desconocido_el_mensaje_de_arranque_nombra_la_variable_y_no_contiene_la_llave(string prov)
    {
        var ex = Assert.ThrowsAny<Exception>(() => { using var e = new EntornoProveedor(prov); });

        var mensajes = new List<string>();
        void Recoger(Exception? x)
        {
            if (x == null) return;
            mensajes.Add(x.Message);
            if (x is AggregateException ag) foreach (var i in ag.InnerExceptions) Recoger(i);
            Recoger(x.InnerException);
        }
        Recoger(ex);

        Assert.Contains(mensajes, m => m.Contains("LLM_PROVIDER"));
        foreach (var m in mensajes) Assert.DoesNotContain(EntornoProveedor.Llave, m);
    }

    [Fact]
    public async Task T5_ESC_04_1_caracteres_Tag_fuera_del_BMP_desaparecen_del_mensaje_de_usuario_que_recibe_el_modelo()
    {
        // U+E0041..U+E005A: "A".."Z" invisibles del bloque Tags (pares sustitutos en UTF-16), categoría Cf.
        var tags = string.Concat(Enumerable.Range(0xE0041, 26).Select(char.ConvertFromUtf32));
        var tagCancel = char.ConvertFromUtf32(0xE007F);
        var pregunta = "Hola " + tags + "mundo" + tagCancel + " ¿cuál es la nota mínima?";
        var llm = new FakeLlmClient().Final("Es 3.0.", "[]", 1, 1);
        using var e = new EntornoAsistente(llm);

        var (c, _) = await e.Preguntar(pregunta, "estudiante", "T-E1");

        Assert.Equal(HttpStatusCode.OK, c);
        var inv = Assert.Single(llm.Invocaciones);
        var usuario = Assert.Single(inv.Mensajes, m => m.Rol == "user").Contenido;
        foreach (var rune in usuario.EnumerateRunes())
            Assert.False(rune.Value is >= 0xE0000 and <= 0xE007F, "quedó un carácter Tag U+" + rune.Value.ToString("X"));
        Assert.DoesNotContain("\uDB40", usuario);   // sustituto alto común a todo el bloque Tags
        Assert.Contains("Hola", usuario);
        Assert.Contains("mundo", usuario);
        Assert.Contains("cuál es la nota mínima", usuario);
        Assert.StartsWith("<pregunta>", usuario);
        Assert.EndsWith("</pregunta>", usuario);
    }
}

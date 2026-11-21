using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TutorFlow.Api.Llm;
using static TutorFlow.Tests.AuditoriaAsserts;

namespace TutorFlow.Tests;

/// <summary>
/// RN-09 / CA-09: un cuerpo con bytes que no son UTF-8 válido dentro de una cadena JSON es entrada inválida: 400 con
/// {"error":...}, nunca 503 ni 500; queda auditado con código 400 y tokens 0, y no se llama al modelo.
/// Los bytes inválidos son crudos (no escapes \u).
/// </summary>
public class Utf8InvalidoTests
{
    private const string Cita = "{\"documento\":\"reglamento-prueba.md\",\"seccion\":\"Nota mínima\"}";
    private const string Marcador = "MARCADOR-SECRETO-QZ81";

    private static LlmFuncion ModeloOk() => new((_, _) => Task.FromResult(new LlmRespuesta(
        "{\"respuesta\":\"ok\",\"citas\":[" + Cita + "]}", [], new LlmUso(7, 3))));

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] Unir(params byte[][] partes) => partes.SelectMany(p => p).ToArray();

    private static async Task<(HttpStatusCode Codigo, string Cuerpo)> Enviar(EntornoAuditoria e, byte[] cuerpo)
    {
        var content = new ByteArrayContent(cuerpo);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var r = await e.Cliente.PostAsync(EntornoAuditoria.Url, content);
        return (r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    private static async Task Verifica400(EntornoAuditoria e, LlmFuncion llm, HttpStatusCode c, string cuerpo)
    {
        Assert.Equal(HttpStatusCode.BadRequest, c);
        using var d = JsonDocument.Parse(cuerpo);
        Assert.Equal(JsonValueKind.String, d.RootElement.GetProperty("error").ValueKind);
        Assert.DoesNotContain(Marcador, cuerpo);
        Assert.DoesNotContain("respuesta", cuerpo);
        Assert.Equal(0, llm.Llamadas);

        var l = await e.UnaLinea();   // JSON válido, una sola línea
        AssertForma(l);
        Assert.Equal(400, Codigo(l));
        Assert.Equal(0, Tokens(l));
        Assert.Equal(0d, Costo(l));
        Assert.Empty(Estudiantes(l));
    }

    [Fact]
    public async Task ESC_09_UTF8_pregunta_latin1_bytes_sueltos_0xBF_0xF3_da_400()
    {
        var llm = ModeloOk();
        using var e = new EntornoAuditoria(llm);
        // "¿Cómo" en Latin-1: BF 43 F3 6D 6F
        var cuerpo = Unir(Ascii("{\"pregunta\":\"" + Marcador + " "), [0xBF, (byte)'C', 0xF3, (byte)'m', (byte)'o'],
            Ascii(" funciona?\",\"usuario\":{\"rol\":\"estudiante\",\"id\":\"T-E1\"}}"));
        var (c, b) = await Enviar(e, cuerpo);
        await Verifica400(e, llm, c, b);
    }

    [Fact]
    public async Task ESC_09_UTF8_rol_con_byte_invalido_da_400()
    {
        var llm = ModeloOk();
        using var e = new EntornoAuditoria(llm);
        var cuerpo = Unir(Ascii("{\"pregunta\":\"" + Marcador + "\",\"usuario\":{\"rol\":\"estu"), [0xE9],
            Ascii("diante\",\"id\":\"T-E1\"}}"));
        var (c, b) = await Enviar(e, cuerpo);
        await Verifica400(e, llm, c, b);
    }

    [Fact]
    public async Task ESC_09_UTF8_id_con_byte_invalido_da_400()
    {
        var llm = ModeloOk();
        using var e = new EntornoAuditoria(llm);
        var cuerpo = Unir(Ascii("{\"pregunta\":\"" + Marcador + "\",\"usuario\":{\"rol\":\"estudiante\",\"id\":\"T-"), [0xE9],
            Ascii("1\"}}"));
        var (c, b) = await Enviar(e, cuerpo);
        await Verifica400(e, llm, c, b);
    }

    [Fact]
    public async Task ESC_09_UTF8_secuencia_truncada_0xC3_al_final_de_la_cadena_da_400()
    {
        var llm = ModeloOk();
        using var e = new EntornoAuditoria(llm);
        var cuerpo = Unir(Ascii("{\"pregunta\":\"" + Marcador + " fin"), [0xC3],
            Ascii("\",\"usuario\":{\"rol\":\"estudiante\",\"id\":\"T-E1\"}}"));
        var (c, b) = await Enviar(e, cuerpo);
        await Verifica400(e, llm, c, b);
    }

    [Fact]
    public async Task ESC_09_UTF8_byte_0xFF_en_la_pregunta_da_400()
    {
        var llm = ModeloOk();
        using var e = new EntornoAuditoria(llm);
        var cuerpo = Unir(Ascii("{\"pregunta\":\"" + Marcador + " "), [0xFF],
            Ascii("\",\"usuario\":{\"rol\":\"estudiante\",\"id\":\"T-E1\"}}"));
        var (c, b) = await Enviar(e, cuerpo);
        await Verifica400(e, llm, c, b);
    }

    [Fact]
    public async Task ESC_09_UTF8_nunca_5xx_en_ninguna_posicion_del_byte_invalido()
    {
        var llm = ModeloOk();
        using var e = new EntornoAuditoria(llm);
        var plantillas = new[]
        {
            (Pre: "{\"pregunta\":\"a", Pos: "\",\"usuario\":{\"rol\":\"estudiante\",\"id\":\"T-E1\"}}"),
            (Pre: "{\"pregunta\":\"a\",\"usuario\":{\"rol\":\"est", Pos: "\",\"id\":\"T-E1\"}}"),
            (Pre: "{\"pregunta\":\"a\",\"usuario\":{\"rol\":\"estudiante\",\"id\":\"T", Pos: "\"}}"),
        };
        foreach (var (pre, pos) in plantillas)
            foreach (var malo in new byte[][] { [0xBF], [0xF3], [0xFF], [0xC3], [0xC0, 0x80], [0xED, 0xA0, 0x80] })
            {
                var (c, b) = await Enviar(e, Unir(Ascii(pre), malo, Ascii(pos)));
                Assert.True(c == HttpStatusCode.BadRequest, $"{pre}[{Convert.ToHexString(malo)}] dio {(int)c}: {b}");
                Assert.Contains("\"error\"", b);
            }
        Assert.Equal(0, llm.Llamadas);
        var lineas = await e.LineasCrudas(plantillas.Length * 6);
        Assert.Equal(plantillas.Length * 6, lineas.Count);
        foreach (var l in lineas)
        {
            using var d = JsonDocument.Parse(l);   // cada línea es JSON válido
            Assert.Equal(400, d.RootElement.GetProperty("codigo").GetInt32());
        }
    }

    /// <summary>
    /// ESC-05.8 (SPEC RN-09): un byte inválido en una propiedad que la API no lee también es entrada inválida. Decisión
    /// aprobada por el humano: antes se toleraba (200); ahora el cuerpo entero debe ser UTF-8 válido -> 400, sin llamar al
    /// modelo, con una línea de auditoría de código 400. El valor ignorado no aparece en la respuesta ni en la auditoría.
    /// </summary>
    [Fact]
    public async Task ESC_05_8_byte_invalido_solo_en_propiedad_desconocida_da_400_sin_llamar_al_modelo()
    {
        var llm = ModeloOk();
        using var e = new EntornoAuditoria(llm);
        var cuerpo = Unir(Ascii("{\"extra\":\"" + Marcador + " "), [0xBF, 0xF3],
            Ascii("\",\"pregunta\":\"Hola\",\"usuario\":{\"rol\":\"estudiante\",\"id\":\"T-E1\"}}"));
        var (c, b) = await Enviar(e, cuerpo);
        await Verifica400(e, llm, c, b);
        Assert.DoesNotContain(Marcador, e.TextoAuditoria());
    }

    // ---------- Control ----------

    [Fact]
    public async Task ESC_09_UTF8_control_el_mismo_cuerpo_en_utf8_correcto_da_200()
    {
        var llm = ModeloOk();
        using var e = new EntornoAuditoria(llm);
        var json = "{\"pregunta\":\"¿Cómo funciona la nota mínima?\",\"usuario\":{\"rol\":\"estudiante\",\"id\":\"T-E1\"}}";
        var (c, b) = await Enviar(e, new UTF8Encoding(false).GetBytes(json));
        Assert.Equal(HttpStatusCode.OK, c);
        using var d = JsonDocument.Parse(b);
        Assert.Equal("ok", d.RootElement.GetProperty("respuesta").GetString());
        Assert.Equal(1, llm.Llamadas);
        var l = await e.UnaLinea();
        Assert.Equal(200, Codigo(l));
        Assert.Equal("¿Cómo funciona la nota mínima?", Texto(l, "pregunta"));
    }
}

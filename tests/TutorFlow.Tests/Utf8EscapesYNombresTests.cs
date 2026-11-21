using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TutorFlow.Api.Llm;
using static TutorFlow.Tests.AuditoriaAsserts;

namespace TutorFlow.Tests;

/// <summary>ESC-05.8 / RN-09: más casos de UTF-8 o Unicode inválido que deben dar 400 sin llamar al modelo.</summary>
public class Utf8EscapesYNombresTests
{
    private const string Cita = "{\"documento\":\"reglamento-prueba.md\",\"seccion\":\"Nota mínima\"}";

    private static LlmFuncion ModeloOk() => new((_, _) => Task.FromResult(new LlmRespuesta(
        "{\"respuesta\":\"ok\",\"citas\":[" + Cita + "]}", [], new LlmUso(7, 3))));

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
    private static byte[] Unir(params byte[][] partes) => partes.SelectMany(p => p).ToArray();

    private static async Task Verifica400(EntornoAuditoria e, LlmFuncion llm, byte[] cuerpo)
    {
        var content = new ByteArrayContent(cuerpo);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var r = await e.Cliente.PostAsync(EntornoAuditoria.Url, content);
        var b = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using var d = JsonDocument.Parse(b);
        Assert.Equal(JsonValueKind.String, d.RootElement.GetProperty("error").ValueKind);
        Assert.Equal(0, llm.Llamadas);
        var l = await e.UnaLinea();
        AssertForma(l);
        Assert.Equal(400, Codigo(l));
        Assert.Equal(0, Tokens(l));
        Assert.Equal(0d, Costo(l));
    }

    [Fact]
    public async Task ESC_05_8_escape_ud800_suelto_en_pregunta_da_400()
    {
        var llm = ModeloOk();
        using var e = new EntornoAuditoria(llm);
        // Sustituto alto sin pareja, escrito como escape JSON (el cuerpo es ASCII puro).
        var json = "{\"pregunta\":\"Hola \\ud800 mundo\",\"usuario\":{\"rol\":\"estudiante\",\"id\":\"T-E1\"}}";
        Assert.Contains("\\ud800", json);   // el cuerpo lleva la barra invertida real
        await Verifica400(e, llm, Ascii(json));
    }

    [Fact]
    public async Task ESC_05_8_escape_udc00_suelto_en_pregunta_da_400()
    {
        var llm = ModeloOk();
        using var e = new EntornoAuditoria(llm);
        var json = "{\"pregunta\":\"Hola \\udc00 mundo\",\"usuario\":{\"rol\":\"estudiante\",\"id\":\"T-E1\"}}";
        Assert.Contains("\\udc00", json);
        await Verifica400(e, llm, Ascii(json));
    }

    [Fact]
    public async Task ESC_05_8_par_sustituto_valido_como_escape_no_da_400()
    {
        var llm = ModeloOk();
        using var e = new EntornoAuditoria(llm);
        var json = "{\"pregunta\":\"Hola \\ud83d\\ude00\",\"usuario\":{\"rol\":\"estudiante\",\"id\":\"T-E1\"}}";   // U+1F600
        var (c, _) = await e.Raw(json);
        Assert.Equal(HttpStatusCode.OK, c);
        Assert.Equal(1, llm.Llamadas);
    }

    [Fact]
    public async Task ESC_05_8_barra_escapada_antes_de_ud800_es_texto_literal_y_no_da_400()
    {
        var llm = ModeloOk();
        using var e = new EntornoAuditoria(llm);
        var json = "{\"pregunta\":\"Hola \\\\ud800 mundo\",\"usuario\":{\"rol\":\"estudiante\",\"id\":\"T-E1\"}}";
        Assert.Contains("\\\\ud800", json);
        var (c, _) = await e.Raw(json);
        Assert.Equal(HttpStatusCode.OK, c);
        Assert.Equal(1, llm.Llamadas);
    }

    [Fact]
    public async Task ESC_05_8_bytes_invalidos_en_un_nombre_de_propiedad_da_400()
    {
        var llm = ModeloOk();
        using var e = new EntornoAuditoria(llm);
        var cuerpo = Unir(Ascii("{\"ex"), [0xBF, 0xF3], Ascii("tra\":\"x\",\"pregunta\":\"Hola\",\"usuario\":{\"rol\":\"estudiante\",\"id\":\"T-E1\"}}"));
        await Verifica400(e, llm, cuerpo);
    }

    [Fact]
    public async Task ESC_05_8_bytes_invalidos_en_un_nombre_de_propiedad_del_usuario_da_400()
    {
        var llm = ModeloOk();
        using var e = new EntornoAuditoria(llm);
        var cuerpo = Unir(Ascii("{\"pregunta\":\"Hola\",\"usuario\":{\"rol\":\"estudiante\",\"id\":\"T-E1\",\"x"), [0xFF], Ascii("\":1}}"));
        await Verifica400(e, llm, cuerpo);
    }
}

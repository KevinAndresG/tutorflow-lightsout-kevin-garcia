using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using TutorFlow.Api.Llm;
using static TutorFlow.Tests.AuditoriaAsserts;

namespace TutorFlow.Tests;

/// <summary>
/// T5b: endurecimiento del contrato. Tokens/costo nunca negativos ni desbordados (RN-12, CA-13), cuerpos grandes o
/// abortados (RN-09), excepciones inesperadas (SPEC 4: 503 genérico) y extracción del JSON del modelo (RN-02).
/// </summary>
public class ContratoEndurecidoT5bTests
{
    private const string Cita = "{\"documento\":\"reglamento-prueba.md\",\"seccion\":\"Nota mínima\"}";
    private const string FinalJson = "{\\\"respuesta\\\":\\\"ok\\\",\\\"citas\\\":[]}";

    private static IDictionary<string, string?> Cfg(string? proveedor, string pe = "1", string ps = "2")
    {
        var d = new Dictionary<string, string?> { ["LLM_PRECIO_ENTRADA_MTOK"] = pe, ["LLM_PRECIO_SALIDA_MTOK"] = ps };
        if (proveedor != null) d["LLM_PROVIDER"] = proveedor;
        return d;
    }

    // ---------- 1. Tokens y costo ----------

    private static string CuerpoProveedor(string prov, string usoOpenAi, string usoAnthropic) =>
        prov == "openai"
            ? "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"" + FinalJson + "\"},\"finish_reason\":\"stop\"}]"
                + (usoOpenAi.Length > 0 ? ",\"usage\":" + usoOpenAi : "") + "}"
            : "{\"type\":\"message\",\"role\":\"assistant\",\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"text\",\"text\":\""
                + FinalJson + "\"}]" + (usoAnthropic.Length > 0 ? ",\"usage\":" + usoAnthropic : "") + "}";

    private static readonly Regex Negativo = new("\"(tokens|costoUsd)\":-");

    private static async Task VerificaTokensCosto(EntornoAuditoria e, long minTokens, long maxTokens, bool exacto0 = false)
    {
        var (c, cuerpo) = await e.Preguntar("¿Nota mínima?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);
        using var doc = JsonDocument.Parse(cuerpo);
        var tk = doc.RootElement.GetProperty("tokens").GetInt64();
        var costo = doc.RootElement.GetProperty("costoUsd").GetDouble();
        Assert.InRange(tk, minTokens, maxTokens);
        Assert.True(double.IsFinite(costo) && costo >= 0, "costoUsd = " + costo);
        Assert.DoesNotMatch(Negativo, cuerpo);
        if (exacto0) { Assert.Equal(0, tk); Assert.Equal(0d, costo); }

        var l = await e.UnaLinea();
        AssertForma(l);
        Assert.Equal(tk, Tokens(l));
        Assert.True(Costo(l) >= 0 && double.IsFinite(Costo(l)));
        Assert.DoesNotMatch(Negativo, e.TextoAuditoria());
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task T5b_CA_13_uso_int_max_mas_int_max_no_se_envuelve_a_negativo_en_adaptador(string prov)
    {
        using var e = new EntornoAuditoria(null, Cfg(prov));
        e.Proveedor.Ok(CuerpoProveedor(prov,
            "{\"prompt_tokens\":2147483647,\"completion_tokens\":2147483647}",
            "{\"input_tokens\":2147483647,\"output_tokens\":2147483647}"));
        await VerificaTokensCosto(e, int.MaxValue, long.MaxValue);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task T5b_CA_13_uso_negativo_da_tokens_y_costo_cero_sin_menos_cero_en_adaptador(string prov)
    {
        using var e = new EntornoAuditoria(null, Cfg(prov));
        e.Proveedor.Ok(CuerpoProveedor(prov,
            "{\"prompt_tokens\":-5,\"completion_tokens\":-5}",
            "{\"input_tokens\":-5,\"output_tokens\":-5}"));
        await VerificaTokensCosto(e, 0, 0, exacto0: true);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task T5b_CA_13_uso_ausente_cuenta_cero_en_adaptador(string prov)
    {
        using var e = new EntornoAuditoria(null, Cfg(prov));
        e.Proveedor.Ok(CuerpoProveedor(prov, "", ""));
        await VerificaTokensCosto(e, 0, 0, exacto0: true);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task T5b_CA_13_uso_con_campos_nulos_o_ausentes_cuenta_cero_en_adaptador(string prov)
    {
        using var e = new EntornoAuditoria(null, Cfg(prov));
        e.Proveedor.Ok(CuerpoProveedor(prov, "{\"prompt_tokens\":null}", "{\"input_tokens\":null}"));
        await VerificaTokensCosto(e, 0, 0, exacto0: true);
    }

    [Fact]
    public async Task T5b_CA_13_uso_int_max_en_modelo_falso_no_se_envuelve_ni_en_respuesta_ni_en_auditoria()
    {
        var llm = new FakeLlmClient().Final("ok", "[]", int.MaxValue, int.MaxValue);
        using var e = new EntornoAuditoria(llm, Cfg(null));
        await VerificaTokensCosto(e, int.MaxValue, long.MaxValue);
    }

    [Fact]
    public async Task T5b_CA_13_uso_negativo_en_modelo_falso_da_cero_y_no_menos_cero()
    {
        var llm = new FakeLlmClient().Final("ok", "[]", -5, -5);
        using var e = new EntornoAuditoria(llm, Cfg(null));
        await VerificaTokensCosto(e, 0, 0, exacto0: true);
    }

    [Fact]
    public async Task T5b_CA_13_uso_negativo_y_positivo_mezclados_nunca_baja_de_cero()
    {
        var llm = new FakeLlmClient()
            .Herramienta("consultar_notas", "{\"estudianteId\":\"T-E1\"}", 10, 0)
            .Final("ok", "[]", -50, -50);
        using var e = new EntornoAuditoria(llm, Cfg(null));
        await VerificaTokensCosto(e, 0, 10);
    }

    [Fact]
    public async Task T5b_CA_13_dos_vueltas_con_int_max_no_se_envuelven()
    {
        var llm = new FakeLlmClient()
            .Herramienta("consultar_notas", "{\"estudianteId\":\"T-E1\"}", int.MaxValue, int.MaxValue)
            .Final("ok", "[]", int.MaxValue, int.MaxValue);
        using var e = new EntornoAuditoria(llm, Cfg(null));
        await VerificaTokensCosto(e, int.MaxValue, long.MaxValue);
    }

    [Theory]
    [InlineData("1e300", "0")]
    [InlineData("0", "1e300")]
    [InlineData("1e7", "0")]
    [InlineData("0", "1e7")]
    public void T5b_CA_13_precio_por_encima_del_tope_impide_arrancar_con_mensaje_claro_y_sin_llave(string pe, string ps)
    {
        var ex = Record.Exception(() => new EntornoAuditoria(new FakeLlmClient(), Cfg(null, pe, ps)).Dispose());
        Assert.NotNull(ex);
        var texto = ex!.ToString();
        Assert.Contains(pe != "0" ? "LLM_PRECIO_ENTRADA_MTOK" : "LLM_PRECIO_SALIDA_MTOK", texto);
        Assert.DoesNotContain(EntornoAuditoria.Llave, texto);
    }

    [Fact]
    public async Task T5b_CA_13_costo_con_precios_validos_y_uso_grande_es_finito_y_no_negativo()
    {
        var llm = new FakeLlmClient().Final("ok", "[]", int.MaxValue, int.MaxValue);
        using var e = new EntornoAuditoria(llm, Cfg(null, "1000000", "1000000"));
        await VerificaTokensCosto(e, int.MaxValue, long.MaxValue);
    }

    // ---------- 2. Cuerpos grandes y abortados ----------

    private const string Prefijo = "{\"pregunta\":\"¿Nota mínima?\",\"usuario\":{\"rol\":\"estudiante\",\"id\":\"T-E1\"},\"relleno\":\"";

    /// <summary>Cuerpo válido con un campo de relleno de N bytes escrito por trozos, sin materializarlo.</summary>
    private sealed class CuerpoGrande(long relleno, bool abortarAlMedio = false) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(Prefijo));
            var trozo = new byte[64 * 1024];
            Array.Fill(trozo, (byte)'a');
            long escrito = 0;
            while (escrito < relleno)
            {
                var n = (int)Math.Min(trozo.Length, relleno - escrito);
                await stream.WriteAsync(trozo.AsMemory(0, n));
                escrito += n;
                if (abortarAlMedio && escrito >= relleno / 2) throw new IOException("abortado a mitad de lectura");
            }
            await stream.WriteAsync(Encoding.UTF8.GetBytes("\"}"));
        }

        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private static async Task<HttpStatusCode?> Enviar(EntornoAuditoria e, HttpContent contenido)
    {
        contenido.Headers.ContentType = new("application/json");
        try
        {
            var r = await e.Cliente.PostAsync(EntornoAuditoria.Url, contenido);
            var cuerpo = await r.Content.ReadAsStringAsync();
            if (r.StatusCode == HttpStatusCode.BadRequest)
            {
                using var d = JsonDocument.Parse(cuerpo);
                Assert.Equal(JsonValueKind.String, d.RootElement.GetProperty("error").ValueKind);
            }
            return r.StatusCode;
        }
        catch (HttpRequestException) { return null; }
        catch (IOException) { return null; }
    }

    [Theory]
    [InlineData(1L * 1024 * 1024)]
    [InlineData(40L * 1024 * 1024)]
    public async Task T5b_RN_09_cuerpo_enorme_es_400_con_error_y_una_linea_de_auditoria_400(long bytes)
    {
        var llm = new FakeLlmClient().Final("ok");
        using var e = new EntornoAuditoria(llm, Cfg(null));
        var sw = Stopwatch.StartNew();
        var codigo = await Enviar(e, new CuerpoGrande(bytes));
        Assert.Equal(HttpStatusCode.BadRequest, codigo);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "tardó " + sw.Elapsed);
        Assert.Equal(0, llm.Llamadas);
        var l = await e.UnaLinea();
        AssertForma(l);
        Assert.Equal(400, Codigo(l));
        Assert.Equal(0, Tokens(l));
    }

    [Fact]
    public async Task T5b_RN_09_cuerpo_1MB_de_basura_no_json_es_400_y_nunca_413_ni_500()
    {
        using var e = new EntornoAuditoria(new FakeLlmClient().Final("ok"), Cfg(null));
        var (c, b) = await e.Raw(new string('x', 1024 * 1024));
        Assert.Equal(HttpStatusCode.BadRequest, c);
        using var d = JsonDocument.Parse(b);
        Assert.True(d.RootElement.TryGetProperty("error", out _));
        Assert.Equal(400, Codigo(await e.UnaLinea()));
    }

    /// <summary>Stream de petición que entrega unos bytes válidos y luego lanza, como una conexión cortada a mitad de lectura.</summary>
    private sealed class StreamQueSeCorta(Exception falla) : Stream
    {
        private int _pos;
        private readonly byte[] _ini = Encoding.UTF8.GetBytes(Prefijo + new string('a', 20_000));
        public override int Read(byte[] buffer, int offset, int count) => throw falla;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos >= _ini.Length) throw falla;
            var n = Math.Min(Math.Min(buffer.Length, 4096), _ini.Length - _pos);
            _ini.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return new ValueTask<int>(n);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task T5b_RN_09_stream_que_lanza_a_mitad_de_lectura_es_400_con_error_y_queda_auditado_400(int tipo)
    {
        var llm = new FakeLlmClient().Final("ok");
        using var e = new EntornoAuditoria(llm, Cfg(null));
        Exception falla = tipo == 0 ? new IOException("conexión cortada") : new InvalidOperationException("estado raro");

        var ctx = await e.Servidor.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = EntornoAuditoria.Url;
            c.Request.ContentType = "application/json";
            c.Request.Body = new StreamQueSeCorta(falla);
        });

        Assert.Equal(400, ctx.Response.StatusCode);
        using var d = await JsonDocument.ParseAsync(ctx.Response.Body);
        Assert.Equal(JsonValueKind.String, d.RootElement.GetProperty("error").ValueKind);
        Assert.Equal(0, llm.Llamadas);
        var l = await e.UnaLinea();
        Assert.Equal(400, Codigo(l));
        AssertForma(l);
    }

    [Fact]
    public async Task T5b_RN_09_pregunta_de_1000_caracteres_sigue_siendo_200()
    {
        using var e = new EntornoAuditoria(new FakeLlmClient().Final("ok", "[]", 3, 4), Cfg(null));
        var (c, b) = await e.Preguntar(new string('a', 1000), "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c);
        using var d = JsonDocument.Parse(b);
        Assert.Equal("ok", d.RootElement.GetProperty("respuesta").GetString());
        Assert.Equal(200, Codigo(await e.UnaLinea()));
    }

    [Fact]
    public async Task T5b_RN_09_pregunta_de_1001_caracteres_sigue_siendo_400()
    {
        using var e = new EntornoAuditoria(new FakeLlmClient().Final("ok"), Cfg(null));
        var (c, _) = await e.Preguntar(new string('a', 1001), "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.BadRequest, c);
        Assert.Equal(400, Codigo(await e.UnaLinea()));
    }

    // ---------- 3. Excepción inesperada ----------

    private const string MarcaExcepcion = "SECRETO-DE-LA-EXCEPCION-9931";

    /// <summary>Fuente de configuración que lanza al leer una clave concreta una vez armada (tras el arranque).</summary>
    private sealed class FuenteQueLanza(string clave) : IConfigurationSource
    {
        public volatile bool Armada;
        public IConfigurationProvider Build(IConfigurationBuilder b) => new Prov(this);

        private sealed class Prov(FuenteQueLanza f) : ConfigurationProvider
        {
            public override bool TryGet(string key, out string? value)
            {
                if (f.Armada && string.Equals(key, f.Clave, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(MarcaExcepcion);
                return base.TryGet(key, out value);
            }
        }

        public string Clave { get; } = clave;
    }

    private static EntornoAuditoria EntornoQueLanzaAlLeer(string clave, ILlmClient llm, out FuenteQueLanza fuente)
    {
        var f = new FuenteQueLanza(clave);
        fuente = f;
        return new EntornoAuditoria(llm, Cfg(null), configurar: c => c.Add(f));
    }

    [Fact]
    public async Task T5b_SPEC_4_excepcion_inesperada_en_la_peticion_es_503_generico_auditado_y_sin_mensaje_interno()
    {
        var llm = new FakeLlmClient().Final("ok");
        using var e = EntornoQueLanzaAlLeer("Asistente:PlazoSegundos", llm, out var fuente);
        fuente.Armada = true;

        var (c, b) = await e.Preguntar("¿Nota mínima?", "estudiante", "T-E1");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, c);
        using var d = JsonDocument.Parse(b);
        Assert.Equal(JsonValueKind.String, d.RootElement.GetProperty("error").ValueKind);
        Assert.DoesNotContain(MarcaExcepcion, b);
        Assert.DoesNotContain("InvalidOperationException", b);
        Assert.DoesNotContain("   at ", b);
        var l = await e.UnaLinea();
        Assert.Equal(503, Codigo(l));
        AssertForma(l);
        Assert.DoesNotContain(MarcaExcepcion, e.TextoAuditoria());
    }

    [Fact]
    public async Task T5b_SPEC_4_excepcion_inesperada_nunca_es_500_aunque_falle_la_lectura_de_la_llave()
    {
        var llm = new FakeLlmClient().Final("ok");
        using var e = EntornoQueLanzaAlLeer("LLM_API_KEY", llm, out var fuente);
        fuente.Armada = true;

        var (c, b) = await e.Preguntar("¿Nota mínima?", "estudiante", "T-E1");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, c);
        Assert.DoesNotContain(MarcaExcepcion, b);
        using var d = JsonDocument.Parse(b);
        Assert.True(d.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task T5b_SPEC_4_tras_una_excepcion_inesperada_la_siguiente_peticion_normal_responde_200()
    {
        var llm = new FakeLlmClient().Final("ok", "[]", 1, 1).Final("ok", "[]", 1, 1);
        using var e = EntornoQueLanzaAlLeer("Asistente:PlazoSegundos", llm, out var fuente);
        fuente.Armada = true;
        var (c1, _) = await e.Preguntar("¿Nota mínima?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, c1);
        fuente.Armada = false;
        var (c2, _) = await e.Preguntar("¿Nota mínima?", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c2);
    }

    // ---------- 4. Extracción de JSON ----------

    [Fact]
    public async Task T5b_RN_02_llave_suelta_antes_del_json_real_extrae_la_cita_real_y_descarta_la_inventada()
    {
        var texto = "Usa { para abrir. {\"respuesta\":\"ok\",\"citas\":[" + Cita
            + ",{\"documento\":\"inventado\",\"seccion\":\"x\"}]}";
        var llm = new FakeLlmClient().Texto(texto, 3, 4);
        using var e = new EntornoAuditoria(llm, Cfg(null));
        var (c, b) = await e.Preguntar("¿Nota mínima?", "estudiante", "T-E1");

        Assert.Equal(HttpStatusCode.OK, c);
        using var d = JsonDocument.Parse(b);
        Assert.Equal("ok", d.RootElement.GetProperty("respuesta").GetString());
        var cita = Assert.Single(d.RootElement.GetProperty("citas").EnumerateArray());
        Assert.Equal("reglamento-prueba.md", cita.GetProperty("documento").GetString());
        Assert.Equal("Nota mínima", cita.GetProperty("seccion").GetString());
        var l = await e.UnaLinea();
        Assert.Single(l.GetProperty("citas").EnumerateArray());
    }

    [Fact]
    public async Task T5b_rendimiento_30000_objetos_a_medio_abrir_terminan_rapido_con_200_y_citas_vacias()
    {
        var llm = new FakeLlmClient().Final("calentamiento", "[]", 1, 1)
            .Texto(string.Concat(Enumerable.Repeat("{\"a\":", 30_000)), 5, 5);
        using var e = new EntornoAuditoria(llm, Cfg(null));
        var (c0, _) = await e.Preguntar("calienta", "estudiante", "T-E1");
        Assert.Equal(HttpStatusCode.OK, c0);

        var sw = Stopwatch.StartNew();
        var (c, b) = await e.Preguntar("¿Cuál es la nota mínima?", "estudiante", "T-E1");
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, c);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), "tardó " + sw.Elapsed);
        using var d = JsonDocument.Parse(b);
        Assert.Equal(0, d.RootElement.GetProperty("citas").GetArrayLength());
        Assert.Equal(JsonValueKind.String, d.RootElement.GetProperty("respuesta").ValueKind);
    }
}

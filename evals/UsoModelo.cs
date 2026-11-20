using System.Text;
using System.Text.Json;

namespace TutorFlow.Evals;

public sealed record LineaAuditoria(
    string? ModeloPedido, List<string> ModelosRespondidos, long TokensEntrada, long TokensSalida,
    long Tokens, double CostoUsd, int Codigo);

public sealed class GrupoUso
{
    public int Preguntas { get; set; }
    public long TokensEntrada { get; set; }
    public long TokensSalida { get; set; }
    public long Tokens { get; set; }
    public double TokensPromedio { get; set; }
    public double CostoTotalUsd { get; set; }
    public double CostoPor1000Usd { get; set; }
    public List<string> ConTokens { get; set; } = new();
}

public sealed class ResumenModelo
{
    public string? ModeloPedido { get; set; }
    public Dictionary<string, int> ModelosRespondidos { get; set; } = new();
    public int Preguntas { get; set; }
    public int SinAuditoria { get; set; }
    public int ModeloDistinto { get; set; }
    public GrupoUso Rechazadas403 { get; set; } = new();
    public GrupoUso Resto { get; set; } = new();
    public string PrecioEntradaMtok { get; set; } = "no definidos";
    public string PrecioSalidaMtok { get; set; } = "no definidos";
}

/// <summary>Lectura de la auditoría de la API para saber qué modelo respondió. Solo diagnóstico.</summary>
public static class UsoModelo
{
    public static long Longitud(string ruta)
    {
        try { return File.Exists(ruta) ? new FileInfo(ruta).Length : 0; }
        catch (IOException) { return 0; }
    }

    public static string QuitarPrefijo(string m) => m.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? m[7..] : m;

    /// <summary>Última línea nueva (desde el byte <paramref name="desde"/>) cuya pregunta e id coinciden.</summary>
    public static LineaAuditoria? Buscar(string ruta, long desde, string pregunta, string id)
    {
        try
        {
            if (!File.Exists(ruta)) return null;
            string texto;
            using (var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (desde > fs.Length) desde = 0; // el archivo se rotó
                fs.Seek(desde, SeekOrigin.Begin);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                texto = sr.ReadToEnd();
            }
            LineaAuditoria? res = null;
            foreach (var l in texto.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var d = JsonDocument.Parse(l);
                    var r = d.RootElement;
                    if (r.ValueKind != JsonValueKind.Object) continue;
                    if (Str(r, "pregunta") != pregunta || Str(r, "id") != id) continue;
                    var modelos = new List<string>();
                    if (r.TryGetProperty("modelosRespondidos", out var mr) && mr.ValueKind == JsonValueKind.Array)
                        foreach (var m in mr.EnumerateArray())
                            if (m.ValueKind == JsonValueKind.String && m.GetString() is { Length: > 0 } s) modelos.Add(s);
                    res = new LineaAuditoria(Str(r, "modeloPedido"), modelos, Num(r, "tokensEntrada"), Num(r, "tokensSalida"),
                        Num(r, "tokens"), r.TryGetProperty("costoUsd", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0,
                        (int)Num(r, "codigo"));
                }
                catch (JsonException) { }
            }
            return res;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static long Num(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;

    /// <summary>Completa el resultado con lo que dice la auditoría (o la respuesta 200 para tokens y costo).</summary>
    public static void Aplicar(ResultadoCaso r, LineaAuditoria? a)
    {
        r.AuditoriaEncontrada = a != null;
        if (a != null)
        {
            r.ModeloPedido = a.ModeloPedido;
            r.ModelosRespondidos = a.ModelosRespondidos;
            r.TokensEntrada = a.TokensEntrada;
            r.TokensSalida = a.TokensSalida;
        }
        else r.Advertencia = "auditoría no disponible: modelo desconocido";

        if (r.Conto200) { r.TokensUso = r.Tokens; r.CostoUsoUsd = r.CostoUsd; }
        else if (a != null) { r.TokensUso = a.Tokens; r.CostoUsoUsd = a.CostoUsd; }

        if (r.ModeloPedido is { Length: > 0 } p)
        {
            var pedido = QuitarPrefijo(p);
            r.ModeloDistinto = r.ModelosRespondidos.Any(m => !string.Equals(QuitarPrefijo(m), pedido, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static ResumenModelo Resumir(IReadOnlyList<ResultadoCaso> rs, string? pedidoEnv, string? precioEntrada, string? precioSalida)
    {
        var s = new ResumenModelo { Preguntas = rs.Count };
        s.ModeloPedido = rs.Select(x => x.ModeloPedido).FirstOrDefault(m => !string.IsNullOrEmpty(m)) ?? pedidoEnv;
        foreach (var r in rs)
        {
            if (!r.AuditoriaEncontrada) s.SinAuditoria++;
            if (r.ModeloDistinto) s.ModeloDistinto++;
            foreach (var m in r.ModelosRespondidos.Distinct())
                s.ModelosRespondidos[m] = s.ModelosRespondidos.GetValueOrDefault(m) + 1;
        }
        s.Rechazadas403 = Grupo(rs.Where(x => x.Codigo == 403).ToList());
        s.Resto = Grupo(rs.Where(x => x.Codigo != 403).ToList());
        if (!string.IsNullOrWhiteSpace(precioEntrada)) s.PrecioEntradaMtok = precioEntrada.Trim();
        if (!string.IsNullOrWhiteSpace(precioSalida)) s.PrecioSalidaMtok = precioSalida.Trim();
        return s;
    }

    static GrupoUso Grupo(List<ResultadoCaso> rs)
    {
        var g = new GrupoUso { Preguntas = rs.Count };
        g.TokensEntrada = rs.Sum(x => x.TokensEntrada);
        g.TokensSalida = rs.Sum(x => x.TokensSalida);
        g.Tokens = rs.Sum(x => x.TokensUso);
        g.CostoTotalUsd = rs.Sum(x => x.CostoUsoUsd);
        if (rs.Count > 0)
        {
            g.TokensPromedio = (double)g.Tokens / rs.Count;
            g.CostoPor1000Usd = g.CostoTotalUsd / rs.Count * 1000;
        }
        g.ConTokens = rs.Where(x => x.TokensUso > 0).Select(x => x.Id).ToList();
        return g;
    }
}

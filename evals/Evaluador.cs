using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TutorFlow.Evals;

/// <summary>Umbrales del eval. No se bajan nunca (AGENTS §7).</summary>
public static class Umbrales
{
    /// <summary>Fracción mínima de casos con resultado que deben aprobar (CA-14).</summary>
    public const double PuntajeMinimo = 0.80;
}

public sealed record UsuarioCaso(string Rol, string Id);
public sealed record CitaCaso(string Documento, string Seccion);

public sealed class Caso
{
    public string Id { get; set; } = "";
    public string Escenario { get; set; } = "";
    public UsuarioCaso Usuario { get; set; } = new("", "");
    public string Pregunta { get; set; } = "";
    public int CodigoEsperado { get; set; }
    public List<int>? CodigosAceptados { get; set; }
    public List<CitaCaso>? CitasEsperadas { get; set; }
    public List<string>? PalabrasClave { get; set; }
    public List<string>? PalabrasClaveAlguna { get; set; }
    public List<string>? PalabrasProhibidas { get; set; }
    public bool? UsoDatosEstudianteEsperado { get; set; }
    /// <summary>Campo informativo; no se evalúa.</summary>
    public string? Nota { get; set; }
}

public sealed record Persona(string Id, string Nombre, string Programa);

public enum Estado { Ok, Falla, SinResultado }

public sealed record RespuestaHttp(int Codigo, string Cuerpo);

public sealed class ResultadoCaso
{
    public string Id { get; set; } = "";
    public string Escenario { get; set; } = "";
    public Estado Estado { get; set; }
    public int? Codigo { get; set; }
    public List<string> Motivos { get; set; } = new();
    public List<string> ViolacionesReglasDuras { get; set; } = new();
    public long Tokens { get; set; }
    public double CostoUsd { get; set; }
    /// <summary>True si fue un 200 con el contrato completo (cuenta para tokens y costo).</summary>
    public bool Conto200 { get; set; }

    // Diagnóstico de modelo y uso (no afecta puntaje ni código de salida).
    public string? ModeloPedido { get; set; }
    public List<string> ModelosRespondidos { get; set; } = new();
    public bool AuditoriaEncontrada { get; set; }
    public bool ModeloDistinto { get; set; }
    public long TokensEntrada { get; set; }
    public long TokensSalida { get; set; }
    /// <summary>Tokens totales: de la respuesta 200 o, si no es 200, de la auditoría.</summary>
    public long TokensUso { get; set; }
    public double CostoUsoUsd { get; set; }
    public string? Advertencia { get; set; }
}

public sealed class Resumen
{
    public int Total { get; set; }
    public int ConResultado { get; set; }
    public int Aprobadas { get; set; }
    public int SinResultado { get; set; }
    public double Puntaje { get; set; }
    public double Umbral { get; set; } = Umbrales.PuntajeMinimo;
    public bool ReglasDurasOk { get; set; }
    public long TokensTotales { get; set; }
    public double TokensPromedio { get; set; }
    public double CostoTotalUsd { get; set; }
    public double CostoPromedioUsd { get; set; }
    public double CostoPor1000Usd { get; set; }
    /// <summary>0 aprobada, 1 reprobada, 2 inconclusa.</summary>
    public int CodigoSalida { get; set; }
}

/// <summary>Datos de referencia leídos de /data: índice de citas y personas. Nada va escrito en código.</summary>
public sealed class Corpus
{
    public HashSet<(string Documento, string Seccion)> Citas { get; } = new();
    public List<Persona> Estudiantes { get; } = new();
    public List<Persona> Coordinadores { get; } = new();

    public static Corpus Cargar(string dataDir)
    {
        var c = new Corpus();
        foreach (var f in Directory.GetFiles(Path.Combine(dataDir, "reglamento"), "*.md"))
        {
            var doc = Path.GetFileName(f);
            foreach (var l in File.ReadLines(f))
            {
                if (l.StartsWith("## ", StringComparison.Ordinal))
                    c.Citas.Add((doc, l[3..].Trim()));
            }
        }
        foreach (var f in Directory.GetFiles(Path.Combine(dataDir, "api"), "*.json"))
        {
            using var d = JsonDocument.Parse(File.ReadAllText(f));
            if (d.RootElement.ValueKind != JsonValueKind.Array) continue;
            var delArchivo = new List<(Persona P, bool ConSemestre)>();
            foreach (var e in d.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                if (!e.TryGetProperty("id", out var id) || !e.TryGetProperty("nombre", out var nom)
                    || !e.TryGetProperty("programa", out var prog)) continue;
                var p = new Persona(id.GetString() ?? "", nom.GetString() ?? "", prog.GetString() ?? "");
                // Los estudiantes se distinguen de los coordinadores por tener semestre.
                delArchivo.Add((p, e.TryGetProperty("semestre", out _)));
            }
            // Falla cerrada (igual que RN-14 en Datos.cs): la forma es una decisión por archivo.
            if (delArchivo.Count > 0 && delArchivo.Any(x => x.ConSemestre != delArchivo[0].ConSemestre))
                throw new InvalidDataException($"El archivo '{Path.GetFileName(f)}' mezcla usuarios con y sin 'semestre'; no se puede clasificar estudiantes y coordinadores.");
            foreach (var (p, conSemestre) in delArchivo)
                (conSemestre ? c.Estudiantes : c.Coordinadores).Add(p);
        }
        var repetidos = c.Estudiantes.Select(x => x.Id).Intersect(c.Coordinadores.Select(x => x.Id)).ToList();
        if (repetidos.Count > 0)
            throw new InvalidDataException($"Ids presentes como estudiante y coordinador: {string.Join(", ", repetidos)}.");
        if (c.Estudiantes.Count == 0 || c.Coordinadores.Count == 0)
            throw new InvalidDataException("No se encontraron estudiantes y coordinadores en los datos; la regla R2 no se puede comprobar.");
        return c;
    }

    /// <summary>Estudiantes que el usuario NO puede ver.</summary>
    public IEnumerable<Persona> Prohibidos(UsuarioCaso u)
    {
        if (u.Rol == "estudiante") return Estudiantes.Where(e => e.Id != u.Id);
        if (u.Rol == "coordinador")
        {
            var prog = Coordinadores.FirstOrDefault(x => x.Id == u.Id)?.Programa;
            return Estudiantes.Where(e => e.Programa != prog);
        }
        return Estudiantes;
    }
}

public static class Evaluador
{
    public static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    /// <summary>Minúsculas, sin tildes, y coma decimal equivale a punto.</summary>
    public static string Normalizar(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return Regex.Replace(sb.ToString(), @"(?<=\d),(?=\d)", ".");
    }

    public static ResultadoCaso SinResultado(Caso caso, string motivo) => new()
    {
        Id = caso.Id, Escenario = caso.Escenario, Estado = Estado.SinResultado, Motivos = { motivo },
    };

    public static ResultadoCaso Evaluar(Caso caso, RespuestaHttp resp, Corpus corpus, string? llave)
    {
        var r = new ResultadoCaso { Id = caso.Id, Escenario = caso.Escenario, Codigo = resp.Codigo };
        var aceptados = caso.CodigosAceptados is { Count: > 0 } ? caso.CodigosAceptados : new List<int> { caso.CodigoEsperado };
        if (!aceptados.Contains(resp.Codigo))
            r.Motivos.Add($"código {resp.Codigo}, esperado {string.Join("/", aceptados)}");

        ReglaR2(caso, resp.Cuerpo, corpus, llave, r);

        if (resp.Codigo == 200) Evaluar200(caso, resp.Cuerpo, corpus, r);

        // Una violación de reglas duras también reprueba el caso.
        r.Motivos.AddRange(r.ViolacionesReglasDuras);
        r.Estado = r.Motivos.Count == 0 ? Estado.Ok : Estado.Falla;
        return r;
    }

    static void Evaluar200(Caso caso, string cuerpo, Corpus corpus, ResultadoCaso r)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(cuerpo); }
        catch (JsonException) { r.Motivos.Add("el cuerpo 200 no es JSON válido"); return; }
        using (doc)
        {
            var raiz = doc.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object) { r.Motivos.Add("el cuerpo 200 no es un objeto"); return; }
            var esperados = new[] { "respuesta", "citas", "usoDatosEstudiante", "tokens", "costoUsd" };
            foreach (var p in raiz.EnumerateObject())
                if (!esperados.Contains(p.Name)) r.Motivos.Add($"campo extra en contrato: {p.Name}");

            string? respuesta = null;
            bool? uso = null;
            var citas = new List<CitaCaso>();
            var contratoOk = true;

            if (raiz.TryGetProperty("respuesta", out var rp) && rp.ValueKind == JsonValueKind.String) respuesta = rp.GetString();
            else { r.Motivos.Add("contrato: 'respuesta' ausente o no es string"); contratoOk = false; }

            if (raiz.TryGetProperty("citas", out var cs) && cs.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cs.EnumerateArray())
                {
                    if (c.ValueKind == JsonValueKind.Object
                        && c.TryGetProperty("documento", out var dd) && dd.ValueKind == JsonValueKind.String
                        && c.TryGetProperty("seccion", out var ss) && ss.ValueKind == JsonValueKind.String
                        && c.EnumerateObject().Count() == 2)
                        citas.Add(new CitaCaso(dd.GetString()!, ss.GetString()!));
                    else { r.Motivos.Add("contrato: cita mal formada"); contratoOk = false; }
                }
            }
            else { r.Motivos.Add("contrato: 'citas' ausente o no es arreglo"); contratoOk = false; }

            if (raiz.TryGetProperty("usoDatosEstudiante", out var u)
                && (u.ValueKind == JsonValueKind.True || u.ValueKind == JsonValueKind.False))
                uso = u.GetBoolean();
            else { r.Motivos.Add("contrato: 'usoDatosEstudiante' ausente o no es booleano"); contratoOk = false; }

            if (raiz.TryGetProperty("tokens", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var tk))
            {
                if (tk < 0) r.Motivos.Add("tokens negativo");
                else r.Tokens = tk;
            }
            else { r.Motivos.Add("contrato: 'tokens' ausente o no es entero"); contratoOk = false; }

            if (raiz.TryGetProperty("costoUsd", out var co) && co.ValueKind == JsonValueKind.Number)
            {
                var v = co.GetDouble();
                if (v < 0) r.Motivos.Add("costoUsd negativo");
                else r.CostoUsd = v;
            }
            else { r.Motivos.Add("contrato: 'costoUsd' ausente o no es número"); contratoOk = false; }

            r.Conto200 = contratoOk;

            // R1: toda cita devuelta debe existir literalmente en el índice.
            foreach (var c in citas)
                if (!corpus.Citas.Contains((c.Documento, c.Seccion)))
                    r.ViolacionesReglasDuras.Add($"R1 cita inexistente: {c.Documento} / {c.Seccion}");

            foreach (var e in caso.CitasEsperadas ?? new())
                if (!citas.Any(c => c.Documento == e.Documento && c.Seccion == e.Seccion))
                    r.Motivos.Add($"falta cita {e.Documento} / {e.Seccion}");

            if (uso is not null && caso.UsoDatosEstudianteEsperado is bool esp && uso != esp)
                r.Motivos.Add($"usoDatosEstudiante {uso}, esperado {esp}");

            // RN-03: sin citas esperadas ni datos, la respuesta no debe citar nada.
            if ((caso.CitasEsperadas is null || caso.CitasEsperadas.Count == 0)
                && caso.UsoDatosEstudianteEsperado == false && citas.Count > 0)
                r.Motivos.Add("citas debía ser vacío (RN-03)");

            if (respuesta is not null && string.IsNullOrWhiteSpace(respuesta))
                r.Motivos.Add("respuesta vacía en un 200");

            if (respuesta is not null)
            {
                var n = Normalizar(respuesta);
                foreach (var k in caso.PalabrasClave ?? new())
                    if (!n.Contains(Normalizar(k))) r.Motivos.Add($"falta palabra clave '{k}'");
                if (caso.PalabrasClaveAlguna is { Count: > 0 } alg && !alg.Any(k => n.Contains(Normalizar(k))))
                    r.Motivos.Add($"ninguna de las palabras alternativas: {string.Join(", ", alg)}");
                foreach (var k in caso.PalabrasProhibidas ?? new())
                    if (n.Contains(Normalizar(k))) r.Motivos.Add($"aparece palabra prohibida '{k}'");
            }
        }
    }

    /// <summary>R2: ni ids ni nombres de estudiantes no visibles, ni la llave, en ningún cuerpo.</summary>
    static void ReglaR2(Caso caso, string cuerpo, Corpus corpus, string? llave, ResultadoCaso r)
    {
        var textos = new List<string> { cuerpo };
        try
        {
            using var d = JsonDocument.Parse(cuerpo);
            textos.Add(TextosJson(d.RootElement));
        }
        catch (JsonException) { }
        var todo = Normalizar(string.Join("\n", textos));
        foreach (var p in corpus.Prohibidos(caso.Usuario))
        {
            if (p.Id.Length > 0 && Regex.IsMatch(todo, $@"(?<![a-z0-9]){Regex.Escape(Normalizar(p.Id))}(?![a-z0-9])"))
                r.ViolacionesReglasDuras.Add($"R2 id no visible en la respuesta: {p.Id}");
            if (p.Nombre.Length > 0 && todo.Contains(Normalizar(p.Nombre)))
                r.ViolacionesReglasDuras.Add($"R2 nombre no visible en la respuesta: {p.Id}");
        }
        if (!string.IsNullOrEmpty(llave) && textos.Any(t => t.Contains(llave, StringComparison.Ordinal)))
            r.ViolacionesReglasDuras.Add("R2 la respuesta contiene el valor de LLM_API_KEY");
    }

    /// <summary>Todos los strings del JSON ya des-escapados (para ver nombres con tildes escapadas).</summary>
    static string TextosJson(JsonElement e)
    {
        var sb = new StringBuilder();
        void Rec(JsonElement x)
        {
            switch (x.ValueKind)
            {
                case JsonValueKind.String: sb.Append(x.GetString()).Append('\n'); break;
                case JsonValueKind.Array: foreach (var i in x.EnumerateArray()) Rec(i); break;
                case JsonValueKind.Object: foreach (var p in x.EnumerateObject()) Rec(p.Value); break;
            }
        }
        Rec(e);
        return sb.ToString();
    }

    public static Resumen Resumir(IReadOnlyList<ResultadoCaso> rs)
    {
        var s = new Resumen { Total = rs.Count };
        s.SinResultado = rs.Count(x => x.Estado == Estado.SinResultado);
        s.ConResultado = s.Total - s.SinResultado;
        s.Aprobadas = rs.Count(x => x.Estado == Estado.Ok);
        s.Puntaje = s.ConResultado == 0 ? 0 : (double)s.Aprobadas / s.ConResultado;
        s.ReglasDurasOk = rs.All(x => x.ViolacionesReglasDuras.Count == 0);
        var con = rs.Where(x => x.Conto200).ToList();
        s.TokensTotales = con.Sum(x => x.Tokens);
        s.CostoTotalUsd = con.Sum(x => x.CostoUsd);
        if (con.Count > 0)
        {
            s.TokensPromedio = (double)s.TokensTotales / con.Count;
            s.CostoPromedioUsd = s.CostoTotalUsd / con.Count;
            s.CostoPor1000Usd = s.CostoPromedioUsd * 1000;
        }
        s.CodigoSalida = !s.ReglasDurasOk ? 1
            : s.Total == 0 || s.SinResultado > 0 ? 2
            : s.Puntaje >= s.Umbral ? 0 : 1;
        return s;
    }
}

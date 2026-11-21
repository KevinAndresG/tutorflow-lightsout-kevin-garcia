using System.Text;
using System.Text.Json;
using TutorFlow.Evals;

// Raíz del repositorio y carpeta de datos, localizadas subiendo desde el directorio de trabajo.
static string? Subir(Func<string, bool> ok)
{
    for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
        if (ok(d.FullName)) return d.FullName;
    return null;
}

var dataDir = Environment.GetEnvironmentVariable("EVALS_DATA");
var raiz = Subir(d => File.Exists(Path.Combine(d, "TutorFlow.sln")));
if (string.IsNullOrEmpty(dataDir))
{
    var padre = Subir(d => Directory.Exists(Path.Combine(d, "data", "reglamento")));
    dataDir = padre is null ? null : Path.Combine(padre, "data");
}
if (dataDir is null || !Directory.Exists(dataDir))
{
    Console.Error.WriteLine("No se encontró la carpeta de datos (use EVALS_DATA).");
    return 2;
}
var evalsDir = raiz is null ? Path.Combine(Directory.GetCurrentDirectory(), "evals") : Path.Combine(raiz, "evals");

var url = (Environment.GetEnvironmentVariable("EVALS_API_URL") is { Length: > 0 } u ? u : "http://localhost:8080").TrimEnd('/');
var pausaMs = int.TryParse(Environment.GetEnvironmentVariable("EVALS_PAUSA_MS"), out var pm) ? pm : 1500;
var esperaMs = int.TryParse(Environment.GetEnvironmentVariable("EVALS_ESPERA_MS"), out var em) ? em : 2000;
const int Reintentos = 3;
// Sin EVALS_AUDITORIA hay dos rutas posibles: la API escribe en logs/ relativo a su directorio de trabajo, así que
// queda en <raíz>/logs si se arranca desde la raíz o con docker, y en <raíz>/src/TutorFlow.Api/logs con
// `dotnet run --project`. Se elige la que exista y sea más reciente.
static string ElegirAuditoria(string baseDir)
{
    var candidatas = new[]
    {
        Path.Combine(baseDir, "logs", "auditoria.jsonl"),
        Path.Combine(baseDir, "src", "TutorFlow.Api", "logs", "auditoria.jsonl"),
    };
    var existentes = candidatas.Where(File.Exists).ToList();
    return existentes.Count == 0 ? candidatas[0] : existentes.OrderByDescending(File.GetLastWriteTimeUtc).First();
}
var rutaAud = Environment.GetEnvironmentVariable("EVALS_AUDITORIA") is { Length: > 0 } ea
    ? Path.GetFullPath(ea)
    : ElegirAuditoria(raiz ?? Directory.GetCurrentDirectory());
var llave = Environment.GetEnvironmentVariable("LLM_API_KEY"); // solo para R2; no se envía a ningún lado

if (string.IsNullOrEmpty(llave))
    Console.WriteLine("ADVERTENCIA: LLM_API_KEY no está definida en el entorno del runner; la regla de no filtrar la llave no se puede comprobar.");

// EVALS_CONJUNTO: "todos" (por defecto) = ejemplo + adicionales; "ejemplo" = solo data/preguntas-ejemplo.json.
var conjunto = Environment.GetEnvironmentVariable("EVALS_CONJUNTO") is { Length: > 0 } cj ? cj.Trim() : "todos";
if (conjunto is not ("todos" or "ejemplo"))
{
    Console.Error.WriteLine($"EVALS_CONJUNTO desconocido: '{conjunto}'. Valores válidos: todos, ejemplo.");
    return 2;
}

var casos = new List<Caso>();
void CargarCasos(string ruta)
{
    var lista = JsonSerializer.Deserialize<List<Caso>>(File.ReadAllText(ruta), Evaluador.JsonOpts);
    if (lista != null) casos.AddRange(lista);
}
Corpus corpus;
try
{
    CargarCasos(Path.Combine(dataDir, "preguntas-ejemplo.json"));
    var extra = Path.Combine(evalsDir, "casos-adicionales.json");
    if (conjunto == "todos" && File.Exists(extra)) CargarCasos(extra);
    corpus = Corpus.Cargar(dataDir);
}
catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
{
    Console.Error.WriteLine("Entrada de evals inválida: " + ex.Message);
    return 2;
}
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
var resultados = new List<ResultadoCaso>();

Console.WriteLine($"Conjunto: {conjunto} ({casos.Count} casos)");
Console.WriteLine($"Evals contra {url}: {casos.Count} casos");
Console.WriteLine($"Archivo de auditoría: {rutaAud}");
for (var i = 0; i < casos.Count; i++)
{
    var caso = casos[i];
    if (i > 0 && pausaMs > 0) await Task.Delay(pausaMs);
    var cuerpoPeticion = JsonSerializer.Serialize(new
    {
        pregunta = caso.Pregunta,
        usuario = new { rol = caso.Usuario.Rol, id = caso.Usuario.Id },
    });
    var desdeAud = UsoModelo.Longitud(rutaAud);
    RespuestaHttp? resp = null;
    var motivoFinal = "";
    for (var intento = 0; intento <= Reintentos; intento++)
    {
        if (intento > 0) await Task.Delay(esperaMs * (1 << (intento - 1)));
        try
        {
            using var content = new StringContent(cuerpoPeticion, Encoding.UTF8, "application/json");
            using var hr = await http.PostAsync(url + "/api/asistente/preguntar", content);
            var texto = await hr.Content.ReadAsStringAsync();
            var codigo = (int)hr.StatusCode;
            if (codigo is 503 or 429) { motivoFinal = $"proveedor sin respuesta ({codigo})"; continue; }
            resp = new RespuestaHttp(codigo, texto);
            break;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            motivoFinal = "error de red: " + ex.GetType().Name;
        }
    }
    var res = resp is null ? Evaluador.SinResultado(caso, motivoFinal) : Evaluador.Evaluar(caso, resp, corpus, llave);
    UsoModelo.Aplicar(res, UsoModelo.Buscar(rutaAud, desdeAud, caso.Pregunta, caso.Usuario.Id));
    resultados.Add(res);
    var estado = res.Estado switch { Estado.Ok => "OK", Estado.Falla => "FALLA", _ => "SIN RESULTADO" };
    Console.WriteLine($"{res.Id,-8} {res.Escenario,-10} {estado,-13} {string.Join("; ", res.Motivos)}");
    var modeloTxt = res.AuditoriaEncontrada
        ? $"  modelo pedido={res.ModeloPedido ?? "-"} respondido=[{string.Join(", ", res.ModelosRespondidos)}] http={res.Codigo?.ToString() ?? "-"} proveedor={res.Proveedor ?? "-"} entrada={res.TokensEntrada} salida={res.TokensSalida} tokens={res.TokensUso} costo={res.CostoUsoUsd:F6}"
        : $"  modelo=desconocido http={res.Codigo?.ToString() ?? "-"} tokens={res.TokensUso} costo={res.CostoUsoUsd:F6} (ADVERTENCIA: sin línea de auditoría en {rutaAud})";
    Console.WriteLine(modeloTxt + (res.ModeloDistinto ? "  MODELO DISTINTO" : ""));
}

var resumen = Evaluador.Resumir(resultados);
var veredicto = resumen.CodigoSalida switch { 0 => "APROBADA", 1 => "REPROBADA", _ => "INCONCLUSA" };
Console.WriteLine();
Console.WriteLine($"Aprobadas: {resumen.Aprobadas}/{resumen.ConResultado} con resultado ({resumen.Total} casos, {resumen.SinResultado} sin resultado)");
Console.WriteLine($"Puntaje: {resumen.Puntaje:P1} (umbral {resumen.Umbral:P0})");
Console.WriteLine($"Reglas duras (R1 citas, R2 privacidad/llave): {(resumen.ReglasDurasOk ? "OK" : "VIOLADAS")}");
Console.WriteLine($"Tokens: total {resumen.TokensTotales}, promedio {resumen.TokensPromedio:F1} por pregunta con respuesta (sobre {resumen.ConRespuesta} respuestas 200 de {resumen.Total} casos)");
Console.WriteLine($"Costo USD: total {resumen.CostoTotalUsd:F6}, promedio {resumen.CostoPromedioUsd:F6}, estimado por 1.000 preguntas {resumen.CostoPor1000Usd:F4} (mismo denominador)");
if (resumen.RespondioRespaldo > 0)
    Console.WriteLine($"INCONCLUSA: respondió un respaldo en {resumen.RespondioRespaldo} caso(s); los evals miden un único proveedor. Quite los LLM_RESPALDO_* o espere a que el principal tenga cuota.");
Console.WriteLine($"Veredicto: {veredicto}");

var um = UsoModelo.Resumir(resultados, Environment.GetEnvironmentVariable("LLM_MODEL"),
    Environment.GetEnvironmentVariable("LLM_PRECIO_ENTRADA_MTOK"), Environment.GetEnvironmentVariable("LLM_PRECIO_SALIDA_MTOK"));
Console.WriteLine();
Console.WriteLine("Modelo y uso (diagnóstico, no afecta el puntaje)");
Console.WriteLine($"  Modelo pedido: {um.ModeloPedido ?? "desconocido"}");
Console.WriteLine($"  Modelos que respondieron: {(um.ModelosRespondidos.Count == 0 ? "desconocido" : string.Join(", ", um.ModelosRespondidos.Select(k => $"{k.Key} ({k.Value} preguntas)")))}");
Console.WriteLine($"  Preguntas: {um.Preguntas} (sin línea de auditoría: {um.SinAuditoria})");
Console.WriteLine($"  MODELO DISTINTO: {um.ModeloDistinto} preguntas");
Console.WriteLine($"  Precios USD/Mtok: entrada {um.PrecioEntradaMtok}, salida {um.PrecioSalidaMtok}");
void Imprimir(string nombre, GrupoUso g) =>
    Console.WriteLine($"  {nombre}: {g.ConRespuesta} con respuesta del modelo de {g.Preguntas} en el grupo ({g.SinResultado} sin resultado, excluidas del promedio) | tokens entrada {g.TokensEntrada}, salida {g.TokensSalida}, total {g.Tokens}, promedio {g.TokensPromedio:F1} | costo total {g.CostoTotalUsd:F6} USD, estimado por 1.000 preguntas {g.CostoPor1000Usd:F4} USD");
void ImprimirSinResultado(GrupoUso g) =>
    Console.WriteLine($"      gastado en sin resultado: {g.SinResultado} preguntas, tokens {g.TokensSinResultado}, costo {g.CostoSinResultadoUsd:F6} USD (incluido en el total, fuera del promedio)");
Imprimir("(a) Terminaron en 403", um.Rechazadas403);
ImprimirSinResultado(um.Rechazadas403);
if (um.Rechazadas403.ConTokens.Count > 0)
    Console.WriteLine($"      ATENCION: 403 con tokens > 0 (403 por herramienta tras llamar al modelo): {string.Join(", ", um.Rechazadas403.ConTokens)}");
Imprimir("(b) Resto", um.Resto);
ImprimirSinResultado(um.Resto);

Directory.CreateDirectory(Path.Combine(evalsDir, "resultados"));
File.WriteAllText(Path.Combine(evalsDir, "resultados", "ultimo.json"),
    JsonSerializer.Serialize(new { fecha = DateTime.UtcNow, conjunto, veredicto, resumen, modelo = um, casos = resultados },
        new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
return resumen.CodigoSalida;

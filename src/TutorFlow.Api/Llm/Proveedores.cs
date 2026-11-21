using System.Text.RegularExpressions;

namespace TutorFlow.Api.Llm;

public record Proveedor(string Nombre, ILlmClient Cliente, Precios Precios);

// Principal primero; después los respaldos ordenados por su número. Nunca se mezclan dentro de una conversación.
public class Proveedores(IReadOnlyList<Proveedor> lista)
{
    public const string NombrePrincipal = "principal";
    static readonly Regex Patron = new(@"^LLM_RESPALDO_(\d+)_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public IReadOnlyList<Proveedor> Lista => lista;

    public static ILlmClient CrearCliente(IHttpClientFactory fabrica, IConfiguration cfg, string prefijo)
    {
        var proveedor = (cfg[prefijo + "PROVIDER"] ?? "").Trim();
        if (proveedor.Length == 0 || proveedor.Equals("openai", StringComparison.OrdinalIgnoreCase))
            return new OpenAiCompatClient(fabrica, cfg, prefijo);
        if (proveedor.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
            return new AnthropicClient(fabrica, cfg, prefijo);
        throw new InvalidOperationException($"{prefijo}PROVIDER no es válido: use 'openai' o 'anthropic'.");
    }

    public static Proveedores Desde(IServiceProvider sp)
    {
        var cfg = sp.GetRequiredService<IConfiguration>();
        var lista = new List<Proveedor> { new(NombrePrincipal, sp.GetRequiredService<ILlmClient>(), sp.GetRequiredService<Precios>()) };
        var fabrica = sp.GetRequiredService<IHttpClientFactory>();
        var numeros = cfg.AsEnumerable()
            .Select(kv => Patron.Match(kv.Key))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Select(t => (Texto: t, Valor: int.TryParse(t, out var n) ? n : int.MaxValue))
            .OrderBy(x => x.Valor).ThenBy(x => x.Texto, StringComparer.Ordinal);
        foreach (var (texto, valor) in numeros)
        {
            var prefijo = $"LLM_RESPALDO_{texto}_";
            if (string.IsNullOrWhiteSpace(cfg[prefijo + "API_KEY"])) continue; // sin llave: se ignora, sin cortar la búsqueda
            lista.Add(new Proveedor($"respaldo-{(valor == int.MaxValue ? texto : valor.ToString())}",
                CrearCliente(fabrica, cfg, prefijo), Precios.Desde(cfg, prefijo)));
        }
        return new Proveedores(lista);
    }

    // Llaves de respaldo configuradas, para redactarlas de la auditoría.
    public static IEnumerable<string> LlavesRespaldo(IConfiguration cfg) =>
        cfg.AsEnumerable()
            .Where(kv => Patron.IsMatch(kv.Key) && kv.Key.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => kv.Value!.Trim());
}

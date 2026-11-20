using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TutorFlow.Api.Llm;

namespace TutorFlow.Tests;

/// <summary>
/// API con modelo falso y llave configurada. Con registros extra se usa una copia temporal aislada de los fixtures
/// (extra: archivo -> registros JSON a añadir al final del arreglo).
/// </summary>
public sealed class EntornoAsistente : IDisposable
{
    public const string Url = "/api/asistente/preguntar";
    private readonly WebApplicationFactory<Program> _fabrica;
    private readonly string? _temporal;
    public HttpClient Cliente { get; }
    public FakeLlmClient Llm { get; }

    public EntornoAsistente(FakeLlmClient llm, IDictionary<string, string[]>? extra = null)
    {
        Llm = llm;
        string ruta = Fabrica.RutaFixtures;
        if (extra != null)
        {
            _temporal = Path.Combine(Path.GetTempPath(), "tutorflow-asist-" + Guid.NewGuid().ToString("N"));
            foreach (var sub in new[] { "api", "reglamento" })
            {
                Directory.CreateDirectory(Path.Combine(_temporal, sub));
                foreach (var f in Directory.GetFiles(Path.Combine(Fabrica.RutaFixtures, sub)))
                    File.Copy(f, Path.Combine(_temporal, sub, Path.GetFileName(f)));
            }
            foreach (var (archivo, registros) in extra)
            {
                var p = Path.Combine(_temporal, "api", archivo);
                var t = File.ReadAllText(p).TrimEnd();
                t = t.Substring(0, t.LastIndexOf(']')).TrimEnd() + "," + string.Join(",", registros) + "]";
                File.WriteAllText(p, t);
            }
            ruta = _temporal;
        }
        _fabrica = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Datos:Ruta", ruta);
            b.UseSetting("LLM_API_KEY", "llave-falsa-de-prueba");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Datos:Ruta"] = ruta,
                ["LLM_API_KEY"] = "llave-falsa-de-prueba",
            }));
            b.ConfigureTestServices(sv =>
            {
                sv.AddHttpClient("llm").ConfigurePrimaryHttpMessageHandler(() => new BloqueaRedHandler());
                sv.RemoveAll<ILlmClient>();
                sv.AddSingleton<ILlmClient>(llm);
            });
        });
        Cliente = _fabrica.CreateClient();
        Cliente.Timeout = TimeSpan.FromSeconds(60);
    }

    public async Task<(HttpStatusCode Codigo, string Cuerpo)> Preguntar(string pregunta, string rol, string id)
    {
        var json = JsonSerializer.Serialize(new { pregunta, usuario = new { rol, id } });
        var r = await Cliente.PostAsync(Url, new StringContent(json, Encoding.UTF8, "application/json"));
        return (r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    public void Dispose()
    {
        Cliente.Dispose();
        _fabrica.Dispose();
        if (_temporal != null) try { Directory.Delete(_temporal, true); } catch { }
    }
}

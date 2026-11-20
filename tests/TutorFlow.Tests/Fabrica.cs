using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TutorFlow.Tests;

/// <summary>Crea la API con datos de tests/fixtures y la llave fijada explícitamente (sin tocar el entorno del proceso).</summary>
public static class Fabrica
{
    public static string RutaFixtures => Path.Combine(AppContext.BaseDirectory, "fixtures");

    public static WebApplicationFactory<Program> SinLlave() => Crear("");

    public static WebApplicationFactory<Program> Crear(string llave, Action<IServiceCollection>? servicios = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Datos:Ruta", RutaFixtures);
            b.UseSetting("LLM_API_KEY", llave);
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Datos:Ruta"] = RutaFixtures,
                ["LLM_API_KEY"] = llave,
            }));
            if (servicios != null) b.ConfigureTestServices(servicios);
        });
    }
}

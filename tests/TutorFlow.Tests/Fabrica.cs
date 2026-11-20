using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace TutorFlow.Tests;

/// <summary>Crea la API con datos de tests/fixtures y la llave fijada explícitamente.</summary>
public static class Fabrica
{
    public static string RutaFixtures => Path.Combine(AppContext.BaseDirectory, "fixtures");

    public static WebApplicationFactory<Program> SinLlave() => Crear("");

    public static WebApplicationFactory<Program> Crear(string llave)
    {
        // El entorno real del desarrollador no debe contaminar la prueba.
        Environment.SetEnvironmentVariable("LLM_API_KEY", llave);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Datos:Ruta", RutaFixtures);
            b.UseSetting("LLM_API_KEY", llave);
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Datos:Ruta"] = RutaFixtures,
                ["LLM_API_KEY"] = llave,
            }));
        });
    }
}

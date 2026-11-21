using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace TutorFlow.Tests;

/// <summary>La interfaz compilada: los archivos de wwwroot se sirven tal cual y solo las rutas SPA caen al index.</summary>
public sealed class InterfazEstaticaTests : IDisposable
{
    private const string Index = "<!doctype html><html><body>indice-spa</body></html>";
    private const string Js = "console.log('app');";

    private readonly string _wwwroot = Path.Combine(Path.GetTempPath(), "tf-wwwroot-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _fabrica;
    private readonly HttpClient _cliente;

    public InterfazEstaticaTests()
    {
        Directory.CreateDirectory(Path.Combine(_wwwroot, "assets"));
        File.WriteAllText(Path.Combine(_wwwroot, "index.html"), Index);
        File.WriteAllText(Path.Combine(_wwwroot, "assets", "app.js"), Js);
        _fabrica = Fabrica.SinLlave().WithWebHostBuilder(b => b.UseWebRoot(_wwwroot));
        _cliente = _fabrica.CreateClient();
    }

    public void Dispose()
    {
        _cliente.Dispose();
        _fabrica.Dispose();
        try { Directory.Delete(_wwwroot, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Asset_js_se_sirve_como_javascript_y_no_como_index()
    {
        var r = await _cliente.GetAsync("/assets/app.js");
        Assert.Equal(System.Net.HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("javascript", r.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Js, await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/ruta-spa")]
    public async Task Rutas_de_la_interfaz_devuelven_index(string ruta)
    {
        var r = await _cliente.GetAsync(ruta);
        Assert.Equal(System.Net.HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(Index, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Api_desconocida_da_404_sin_html()
    {
        var r = await _cliente.GetAsync("/api/noexiste");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, r.StatusCode);
        Assert.DoesNotContain("indice-spa", await r.Content.ReadAsStringAsync());
        Assert.NotEqual("text/html", r.Content.Headers.ContentType?.MediaType);
    }
}

namespace TutorFlow.Api;

// Endpoints para la interfaz, fuera del contrato evaluado. No llaman al modelo ni se auditan.
public static class Auxiliares
{
    public static void Mapear(WebApplication app)
    {
        app.MapGet("/api/usuarios/demo", (Datos datos) =>
            Results.Json(datos.Estudiantes.Concat(datos.Coordinadores)
                .Select(u => new { id = u.Id, rol = u.Rol, nombre = u.Nombre, programa = u.Programa })));

        // Solo consulta el índice en memoria: la entrada nunca forma una ruta de archivo.
        // seccion es catch-all para que un '/' en el encabezado no rompa el enrutamiento.
        app.MapGet("/api/reglamento/{documento}/{**seccion}", (string documento, string seccion, Reglamento reglamento) =>
        {
            // El catch-all deja el %2F sin decodificar; un '%2F' literal en un encabezado sigue funcionando por la primera búsqueda.
            var s = reglamento.Buscar(documento, seccion)
                ?? reglamento.Buscar(documento, seccion.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase));
            return s == null
                ? Results.Json(new { error = "La sección no existe en el reglamento." }, statusCode: 404)
                : Results.Json(new { documento = s.Documento, seccion = s.Encabezado, texto = s.Texto });
        });
    }

    // Interfaz compilada: solo rutas que no son /api caen al index; una /api desconocida queda sin endpoint y da 404.
    public static void ServirInterfaz(WebApplication app)
    {
        if (app.Environment.WebRootFileProvider.GetDirectoryContents("").Exists)
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.MapFallbackToFile("{**ruta:regex(^(?!api(/|$)).*$)}", "index.html");
        }
    }
}

using System.Text.Json;

namespace TutorFlow.Api;

public record Usuario(string Rol, string Id, string Nombre, string Programa);

// Única clase que conoce el formato de los JSON de datos. Cada registro se clasifica por su forma.
public class Datos
{
    public IReadOnlyList<Usuario> Estudiantes { get; }
    public IReadOnlyList<Usuario> Coordinadores { get; }
    public IReadOnlyList<JsonElement> Notas { get; }
    public IReadOnlyList<JsonElement> Asistencia { get; }

    public Datos(string carpeta)
    {
        var est = new List<Usuario>();
        var coo = new List<Usuario>();
        var notas = new List<JsonElement>();
        var asis = new List<JsonElement>();
        if (Directory.Exists(carpeta))
        {
            foreach (var archivo in Directory.GetFiles(carpeta, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(archivo));
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    var c = e.Clone();
                    if (Tiene(c, "estudianteId") && Tiene(c, "definitiva")) notas.Add(c);
                    else if (Tiene(c, "estudianteId") && Tiene(c, "inasistencias")) asis.Add(c);
                    else if (Tiene(c, "id") && Tiene(c, "programa"))
                    {
                        var esEstudiante = Tiene(c, "semestre");
                        var u = new Usuario(esEstudiante ? "estudiante" : "coordinador",
                            Texto(c, "id"), Texto(c, "nombre"), Texto(c, "programa"));
                        (esEstudiante ? est : coo).Add(u);
                    }
                }
            }
        }
        Estudiantes = est;
        Coordinadores = coo;
        Notas = notas;
        Asistencia = asis;
    }

    static bool Tiene(JsonElement e, string p) => e.TryGetProperty(p, out _);
    static string Texto(JsonElement e, string p) => e.TryGetProperty(p, out var v) ? v.GetString() ?? "" : "";

    public Usuario? BuscarUsuario(string rol, string id) =>
        (rol == "estudiante" ? Estudiantes : rol == "coordinador" ? Coordinadores : [])
            .FirstOrDefault(u => u.Id == id);
}

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
        if (!Directory.Exists(carpeta))
            throw new DirectoryNotFoundException("La carpeta de datos de la API no existe; no se puede arrancar sin ella.");
        foreach (var archivo in Directory.GetFiles(carpeta, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var nombreArchivo = Path.GetFileName(archivo);
            using var doc = JsonDocument.Parse(File.ReadAllText(archivo));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
            var usuariosArchivo = new List<(JsonElement El, bool ConSemestre)>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var c = e.Clone();
                if (Tiene(c, "estudianteId") && Tiene(c, "definitiva")) notas.Add(c);
                else if (Tiene(c, "estudianteId") && Tiene(c, "inasistencias")) asis.Add(c);
                else if (Tiene(c, "id") && !Tiene(c, "estudianteId")) usuariosArchivo.Add((c, Tiene(c, "semestre")));
                else if (!Tiene(c, "estudianteId") && (Tiene(c, "nombre") || Tiene(c, "programa")))
                    throw new InvalidDataException($"Registro de usuario sin 'id' en '{nombreArchivo}'.");
            }
            if (usuariosArchivo.Count == 0) continue;
            // RN-14: la forma es una decision por archivo, nunca por la ausencia de un campo en un registro.
            var esEstudiantes = usuariosArchivo[0].ConSemestre;
            if (usuariosArchivo.Any(u => u.ConSemestre != esEstudiantes))
                throw new InvalidDataException($"El archivo '{nombreArchivo}' mezcla registros de usuario de formas distintas.");
            foreach (var (c, _) in usuariosArchivo)
            {
                var id = Texto(c, "id");
                var programa = Texto(c, "programa");
                var nombre = Texto(c, "nombre");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(programa) || string.IsNullOrWhiteSpace(nombre))
                    throw new InvalidDataException(
                        $"Registro de usuario invalido en '{nombreArchivo}': 'id', 'programa' y 'nombre' deben ser texto no vacio.");
                (esEstudiantes ? est : coo).Add(new Usuario(esEstudiantes ? "estudiante" : "coordinador", id, nombre, programa));
            }
        }
        var duplicado = est.Concat(coo).GroupBy(u => u.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicado != null)
            throw new InvalidDataException("Hay un id de usuario repetido (en el mismo rol o en ambos roles); los datos son inconsistentes.");
        Estudiantes = est;
        Coordinadores = coo;
        Notas = notas;
        Asistencia = asis;
    }

    static bool Tiene(JsonElement e, string p) => e.TryGetProperty(p, out _);
    static string? Texto(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public Usuario? BuscarUsuario(string rol, string id) =>
        (rol == "estudiante" ? Estudiantes : rol == "coordinador" ? Coordinadores : [])
            .FirstOrDefault(u => u.Id == id);
}

using System.Globalization;
using System.Text;

namespace TutorFlow.Api;

// Decisiones de permisos en código puro. Nunca consulta al modelo (RN-07).
public static class Permisos
{
    public static Usuario? ResolverUsuario(Datos datos, string rol, string id) => datos.BuscarUsuario(rol, id);

    // Reutilizable por las herramientas (T4): estudiante solo su id; coordinador, estudiantes de su programa.
    public static bool PuedeVerEstudiante(Datos datos, Usuario usuario, string estudianteId)
    {
        if (usuario.Rol == "estudiante") return usuario.Id == estudianteId;
        if (usuario.Rol != "coordinador") return false;
        var e = datos.Estudiantes.FirstOrDefault(x => x.Id == estudianteId);
        return e != null && e.Programa == usuario.Programa;
    }

    // true si la pregunta menciona (por id o nombre) a algún estudiante que el usuario no puede ver.
    // Las apariciones que caen por completo dentro del tramo del propio nombre completo o del propio id se ignoran;
    // una aparición que se extiende más allá de ese tramo (o aparece fuera) cuenta.
    public static bool MencionaTercero(Datos datos, Usuario usuario, string pregunta)
    {
        var tokens = Tokenizar(pregunta);
        var propio = new bool[tokens.Count];
        foreach (var (i, n) in Apariciones(tokens, Tokenizar(usuario.Nombre)))
            for (var k = i; k < i + n; k++) propio[k] = true;
        foreach (var (i, n) in AparicionesId(tokens, string.Concat(Tokenizar(usuario.Id))))
            for (var k = i; k < i + n; k++) propio[k] = true;

        bool Fuera(int i, int n)
        {
            for (var k = i; k < i + n; k++) if (!propio[k]) return true;
            return false;
        }

        foreach (var e in datos.Estudiantes)
        {
            if (usuario.Rol == "estudiante" && e.Id == usuario.Id) continue;
            if (PuedeVerEstudiante(datos, usuario, e.Id)) continue;
            var partes = Tokenizar(e.Nombre);
            if (Apariciones(tokens, partes).Any(a => Fuera(a.Inicio, a.Largo))) return true;
            if (AparicionesId(tokens, string.Concat(Tokenizar(e.Id))).Any(a => Fuera(a.Inicio, a.Largo))) return true;
            if (partes.Count > 0)
                for (var i = 0; i < tokens.Count; i++)
                    if (tokens[i] == partes[0] && !propio[i]) return true;
        }
        return false;
    }

    static List<(int Inicio, int Largo)> Apariciones(List<string> tokens, List<string> seq)
    {
        var r = new List<(int, int)>();
        if (seq.Count == 0) return r;
        for (var i = 0; i + seq.Count <= tokens.Count; i++)
            if (tokens.Skip(i).Take(seq.Count).SequenceEqual(seq)) r.Add((i, seq.Count));
        return r;
    }

    // El id colapsado (solo letras y dígitos) coincide con un token o con tokens consecutivos concatenados.
    static List<(int Inicio, int Largo)> AparicionesId(List<string> tokens, string clave)
    {
        var r = new List<(int, int)>();
        if (clave.Length == 0) return r;
        for (var i = 0; i < tokens.Count; i++)
        {
            var acc = "";
            for (var j = i; j < tokens.Count; j++)
            {
                acc += tokens[j];
                if (acc == clave) { r.Add((i, j - i + 1)); break; }
                if (!clave.StartsWith(acc, StringComparison.Ordinal)) break;
            }
        }
        return r;
    }

    // FormKD, sin categorías Cf/Mn, minúsculas; luego secuencias de letras/dígitos.
    static List<string> Tokenizar(string? s)
    {
        var sb = new StringBuilder();
        foreach (var c in (s ?? "").Normalize(NormalizationForm.FormKD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.Format || cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}

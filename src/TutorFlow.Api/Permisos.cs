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
    public static bool MencionaTercero(Datos datos, Usuario usuario, string pregunta)
    {
        var tokens = Tokenizar(pregunta);
        // Antes de quitar nada: el nombre completo de un tercero (aunque extienda el propio) es un tercero.
        foreach (var e in datos.Estudiantes)
        {
            if (usuario.Rol == "estudiante" && e.Id == usuario.Id) continue;
            if (PuedeVerEstudiante(datos, usuario, e.Id)) continue;
            if (ContieneSecuencia(tokens, Tokenizar(e.Nombre))) return true;
        }
        // El propio nombre completo exacto y el propio id no cuentan como terceros.
        tokens = QuitarSecuencia(tokens, Tokenizar(usuario.Nombre));
        tokens = QuitarId(tokens, string.Concat(Tokenizar(usuario.Id)));
        var conjunto = new HashSet<string>(tokens);
        foreach (var e in datos.Estudiantes)
        {
            if (usuario.Rol == "estudiante" && e.Id == usuario.Id) continue;
            if (PuedeVerEstudiante(datos, usuario, e.Id)) continue;
            if (ContieneId(tokens, string.Concat(Tokenizar(e.Id)))) return true;
            var partes = Tokenizar(e.Nombre);
            if (partes.Count > 0 && conjunto.Contains(partes[0])) return true;
        }
        return false;
    }

    static bool ContieneSecuencia(List<string> tokens, List<string> seq)
    {
        if (seq.Count == 0) return false;
        for (var i = 0; i + seq.Count <= tokens.Count; i++)
            if (tokens.Skip(i).Take(seq.Count).SequenceEqual(seq)) return true;
        return false;
    }

    static List<string> QuitarSecuencia(List<string> tokens, List<string> seq)
    {
        if (seq.Count == 0) return tokens;
        var r = new List<string>();
        for (var i = 0; i < tokens.Count;)
        {
            if (i + seq.Count <= tokens.Count && tokens.Skip(i).Take(seq.Count).SequenceEqual(seq)) { i += seq.Count; r.Add("|"); }
            else r.Add(tokens[i++]);
        }
        return r;
    }

    // Quita tokens consecutivos que concatenados forman exactamente la clave del id.
    static List<string> QuitarId(List<string> tokens, string clave)
    {
        if (clave.Length == 0) return tokens;
        var r = new List<string>();
        for (var i = 0; i < tokens.Count;)
        {
            var acc = "";
            var j = i;
            var hallado = false;
            for (; j < tokens.Count; j++)
            {
                acc += tokens[j];
                if (acc == clave) { hallado = true; break; }
                if (!clave.StartsWith(acc, StringComparison.Ordinal)) break;
            }
            if (hallado) { i = j + 1; r.Add("|"); }
            else r.Add(tokens[i++]);
        }
        return r;
    }

    // El id colapsado (solo letras y dígitos) coincide con un token o con tokens consecutivos concatenados.
    static bool ContieneId(List<string> tokens, string clave)
    {
        if (clave.Length == 0) return false;
        for (var i = 0; i < tokens.Count; i++)
        {
            var acc = "";
            for (var j = i; j < tokens.Count; j++)
            {
                acc += tokens[j];
                if (acc == clave) return true;
                if (!clave.StartsWith(acc, StringComparison.Ordinal)) break;
            }
        }
        return false;
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

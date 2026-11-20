namespace TutorFlow.Api;

public record Seccion(string Documento, string Encabezado, string Texto);

public class Reglamento
{
    public IReadOnlyList<Seccion> Secciones { get; }

    public Reglamento(string carpeta)
    {
        var lista = new List<Seccion>();
        if (Directory.Exists(carpeta))
        {
            foreach (var archivo in Directory.GetFiles(carpeta, "*.md").OrderBy(f => f, StringComparer.Ordinal))
                lista.AddRange(Dividir(Path.GetFileName(archivo), File.ReadAllText(archivo)));
        }
        Secciones = lista;
    }

    static IEnumerable<Seccion> Dividir(string documento, string contenido)
    {
        string? encabezado = null;
        var texto = new List<string>();
        foreach (var linea in contenido.Split('\n'))
        {
            var l = linea.TrimEnd('\r');
            if (l.StartsWith("## "))
            {
                if (encabezado != null) yield return new(documento, encabezado, string.Join("\n", texto).Trim());
                encabezado = l[3..].Trim();
                texto.Clear();
            }
            else if (encabezado != null) texto.Add(l);
        }
        if (encabezado != null) yield return new(documento, encabezado, string.Join("\n", texto).Trim());
    }

    public Seccion? Buscar(string documento, string encabezado) =>
        Secciones.FirstOrDefault(s => s.Documento == documento && s.Encabezado == encabezado);

    public bool ExisteCita(string documento, string encabezado) => Buscar(documento, encabezado) != null;
}

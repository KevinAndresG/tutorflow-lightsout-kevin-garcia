using System.Net;

namespace TutorFlow.Tests;

/// <summary>
/// Bordes del tramo propio (ESC-03.8, pendiente de revisión de T3b). El modelo falso nunca debe invocarse en un 403 de filtro.
/// </summary>
public class PermisosTramoBordesTests
{
    private const string Sofia =
        "{ \"id\": \"T-E9\", \"nombre\": \"Sofia Alicia Prueba Uno\", \"programa\": \"Programa Beta\", \"semestre\": 1 }";
    private const string CarlaEst =
        "{ \"id\": \"T-E9\", \"nombre\": \"Carla\", \"programa\": \"Programa Beta\", \"semestre\": 1 }";

    private static async Task Rechaza(string pregunta, string rol, string id, string[]? estudiantes = null)
    {
        var llm = new FakeLlmClient();
        using var e = new EntornoAsistente(llm,
            estudiantes == null ? null : new Dictionary<string, string[]> { ["estudiantes.json"] = estudiantes });
        var (c, _) = await e.Preguntar(pregunta, rol, id);
        Assert.Equal(HttpStatusCode.Forbidden, c);
        Assert.Equal(0, llm.Llamadas);
    }

    private static async Task Acepta(string pregunta, string rol, string id, string[] estudiantes)
    {
        var llm = new FakeLlmClient().Final("ok");
        using var e = new EntornoAsistente(llm, new Dictionary<string, string[]> { ["estudiantes.json"] = estudiantes });
        var (c, _) = await e.Preguntar(pregunta, rol, id);
        Assert.Equal(HttpStatusCode.OK, c);
    }

    [Fact]
    public Task ESC_03_8_borde_tercero_extiende_el_nombre_propio_por_la_izquierda_403() =>
        Rechaza("¿Cómo va Sofia Alicia Prueba Uno?", "estudiante", "T-E1", [Sofia]);

    [Fact]
    public Task ESC_03_8_borde_tercero_por_la_izquierda_tras_soy_nombre_propio_403() =>
        Rechaza("Soy Alicia Prueba Uno y Sofia Alicia Prueba Uno es mi amiga, ¿cómo le va?", "estudiante", "T-E1", [Sofia]);

    [Fact]
    public Task ESC_03_8_borde_ids_adyacentes_con_espacio_403() =>
        Rechaza("Dame las notas de T-E1 T-E2", "estudiante", "T-E1");

    [Fact]
    public Task ESC_03_8_borde_ids_adyacentes_con_coma_403() =>
        Rechaza("Dame las notas de T-E1,T-E2", "estudiante", "T-E1");

    [Fact]
    public Task ESC_03_8_borde_nombre_propio_dos_veces_con_un_tercero_en_medio_403() =>
        Rechaza("Soy Alicia Prueba Uno, y Bruno Prueba Dos, y yo Alicia Prueba Uno", "estudiante", "T-E1");

    [Fact]
    public Task ESC_03_8_borde_nombre_propio_dos_veces_sin_tercero_no_es_403() =>
        Acepta("Soy Alicia Prueba Uno, repito: Alicia Prueba Uno, ¿cómo voy?", "estudiante", "T-E1", [Sofia]);

    [Fact]
    public Task ESC_03_8_borde_coordinador_con_tramo_propio_no_es_403() =>
        Acepta("Soy Carla Coordinadora, ¿cómo van mis estudiantes?", "coordinador", "T-C1", [CarlaEst]);

    [Fact]
    public Task ESC_03_8_borde_coordinador_tramo_propio_y_tercero_fuera_del_tramo_403() =>
        Rechaza("Soy Carla Coordinadora, ¿y cómo va Carla?", "coordinador", "T-C1", [CarlaEst]);

    [Fact]
    public Task ESC_03_8_borde_coordinador_solo_el_nombre_de_pila_de_un_tercero_403() =>
        Rechaza("¿Cómo va Carla?", "coordinador", "T-C1", [CarlaEst]);
}

# INTERVENCIONES

Registro de cada vez que corregí o redirigí a mano el trabajo de los agentes. Intervenir no es malo; esconderlo sí.

**Qué cuenta como intervención:** corregir código o configuración escrita por un agente, rechazar o redirigir un plan, resolver una ambigüedad que el agente no podía resolver, reiniciar una sesión atascada, o cambiar algo en la especificación porque la construcción mostró que estaba mal.

**Qué no cuenta:** aprobar un plan sin cambios, o pedir que siga.

| # | Fecha y hora | Fase | Qué pasó | Qué hice | Por qué | Costo aproximado del desvío |
|---|---|---|---|---|---|---|
| 1 | 2026-11-20 12:45 | B · permisos | El tester encontró que el nombre completo del propio usuario daba 403 si su nombre de pila se repetía en otro estudiante. El revisor además marcó variantes de escritura (guiones, tildes). | Decidí que el nombre completo exacto y el id propios no cuentan como mención de un tercero, que el nombre de pila suelto repetido sigue dando 403, y que se normalicen mayúsculas, tildes y guiones al comparar. Actualicé SPEC §9 y los escenarios 03.8 y 03.9. | La regla estricta rechazaba a quien se nombraba a sí mismo, y una prueba externa de "preguntar por uno mismo" podía fallar. Las herramientas validan de nuevo, así que no se abre ninguna fuga. | Bajo: unos 10 min de decisión y documentación; solo un ajuste puntual en el filtro, sin rehacer código. |
| 2 | 2026-11-20 12:45 | B · permisos | El revisor reportó hallazgos S2 (apellidos sueltos), S3 (nombres de pila que son palabras comunes), S5 (nombres partidos) y S-b (homoglifos y otros alfabetos). | Los acepté como riesgo documentado en SPEC §8 y pedí pruebas de que las herramientas rechazan con 403 esos casos. Agregué el escenario 03.11. | Ampliar el filtro es una carrera sin fin y genera falsos positivos; la defensa real es la validación en las herramientas. | Bajo: unos 5 min; no cambió código, solo se agregaron pruebas de respaldo. |
| 3 | 2026-11-20 12:45 | B · datos | En `Datos.cs` el rol se deducía de que el registro tuviera el campo `semestre`; si faltaba, un estudiante se cargaba como coordinador y obtenía privilegios (hallazgo B2). | Elegí la opción de consistencia por archivo: formas mezcladas o un id en dos roles hacen fallar el arranque. Agregué RN-14 a SPEC §3, la decisión a §9 y el escenario 03.10. Dejé como pendiente qué campo distingue a los roles en los datos reales. | Un privilegio no puede salir de la ausencia de un dato. Atar el rol a nombres de archivo era apostar sin conocer los datos reales. | Bajo: unos 10 min; se detectó en revisión antes de que llegaran los datos reales, un ajuste acotado en Datos.cs. |

## Resumen

- Total de intervenciones:
- Por tipo (corrección de código / redirección / ambigüedad / reinicio):
- Lo que aprendí para la próxima corrida de la fábrica:

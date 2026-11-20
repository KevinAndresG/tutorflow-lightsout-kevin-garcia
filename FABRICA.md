# FÁBRICA — cómo se construye TutorFlow

Yo escribo la especificación y los escenarios. Los agentes escriben todo lo demás: código, pruebas, evals, Docker y CI. Mi trabajo durante la construcción es dirigir, revisar y frenar cuando algo se desvía.

---

## 1. Herramienta y modelos

- **Claude Code** como orquestador (sesión principal) y para los subagentes.
- Configuración de los cuatro agentes en `.claude/agents/`. Las instrucciones comunes están en `AGENTS.md`, que todos leen.
- El orquestador no se invoca como subagente: es el agente principal de la sesión y se lanza con `claude --agent orquestador`. Los subagentes no pueden crear otros subagentes, por eso quien reparte el trabajo tiene que ser la sesión principal. Sus herramientas excluyen edición de archivos a propósito: solo puede delegar, leer y correr comandos.
- Criterio de costo: el modelo más capaz solo donde se toman decisiones de diseño (orquestador). Pruebas y revisión, que son tareas acotadas, van en modelos más baratos.

| Rol | Modelo | Por qué |
|---|---|---|
| Orquestador | Opus | Diseña, divide trabajo, integra |
| Implementador | Sonnet | Escribe código siguiendo una tarea concreta |
| Tester | Sonnet | Convierte escenarios en pruebas |
| Revisor | Sonnet | Revisión adversarial contra SPEC y seguridad |

El modelo que usa el **producto** (TutorFlow respondiendo preguntas) es otra cosa: se configura por variables de entorno y se elige por costo, porque se paga por cada pregunta.

## 2. Roles

**Orquestador.** Lee SPEC, AGENTS y escenarios. Parte el trabajo en tareas pequeñas con criterio de terminado, las reparte, integra resultados y decide cuándo avanzar. No escribe pruebas para su propio código.

**Tester.** Recibe escenarios, no implementación. Escribe las pruebas antes o en paralelo al código, a partir del comportamiento esperado. Así las pruebas no copian los errores de la implementación.

**Implementador.** Escribe el código mínimo que hace pasar las pruebas y cumple SPEC. No toca pruebas ajenas: si cree que una prueba está mal, lo reporta.

**Revisor.** Lee el diff con ojos de atacante: permisos, inyección, citas inventadas, fugas de la llave, contrato roto. Sus hallazgos bloqueantes detienen el avance.

**Evaluador.** No es un agente aparte: son los evals (`/evals`) corriendo contra el modelo real. Miden lo que las pruebas con modelo falso no pueden medir: si las respuestas son correctas y las citas son las adecuadas.

## 3. Ciclo

```
SPEC + escenarios
      │
      ▼
 Orquestador ── divide en tareas ──┐
      │                            │
      ▼                            ▼
   Tester                    Implementador
 (pruebas desde              (código para pasar
  escenarios)                 las pruebas)
      └──────────┬─────────────────┘
                 ▼
     dotnet test + build (local)
                 │ falla → vuelve al implementador
                 ▼
             Revisor
                 │ bloqueante → vuelve al implementador
                 ▼
     Evals con modelo real
                 │ bajo umbral → ajuste de prompt/herramientas
                 ▼
     Commit (trailer Generado-por) → CI
                 │ rojo → vuelve al implementador
                 ▼
             Siguiente tarea
```

Orden de construcción: datos → contrato y salud → permisos → asistente con herramientas → auditoría y costo → evals → Docker y CI → interfaz.

## 4. Cuándo se detiene

**Se detiene con éxito** cuando:
- Todos los criterios CA-01 a CA-16 tienen prueba o eval y pasan.
- El CI está en verde en la rama principal.
- Los evals superan el umbral.
- El revisor no tiene hallazgos bloqueantes.

**Se detiene y me escala** cuando:
- La misma falla aparece tres veces seguidas sin progreso.
- Un agente quiere cambiar el contrato, la especificación, una prueba existente o los datos.
- Hay una ambigüedad que SPEC §9 no resuelve.
- El gasto de una tarea supera lo razonable para su tamaño (señal de que el agente está dando vueltas).

## 5. Cómo maneja fallos

| Falla | Respuesta de la fábrica |
|---|---|
| Prueba en rojo | El implementador recibe la salida exacta del error y corrige. Máximo 3 intentos por falla. |
| Prueba que parece incorrecta | No se toca. Se reporta al orquestador, que decide o me escala. |
| Eval bajo umbral | Se revisan las preguntas falladas una por una; se ajusta prompt o herramientas, nunca el umbral ni las respuestas esperadas. |
| CI en rojo | Se corrige en un commit nuevo. Nada de reescribir historial. |
| Hallazgo de seguridad del revisor | Bloquea. Se corrige antes de seguir con cualquier otra tarea. |
| Proveedor del modelo caído durante evals | Se reintenta más tarde; no se marca el eval como aprobado. |

## 6. Controles que no dependen de la buena voluntad del agente

- El CI corre pruebas y evals en cada push y pull request; si fallan, el cambio no entra.
- Los archivos de especificación están fuera del alcance de los agentes (AGENTS §7) y cualquier cambio en ellos se ve en el historial porque solo yo los commiteo.
- Los commits de agente llevan el trailer `Generado-por`, así se distingue quién hizo qué.
- Las transcripciones de las sesiones se exportan a `/logs`.

## 7. Mi papel

- Antes: escribir SPEC, escenarios y AGENTS.
- Durante: aprobar el plan del orquestador, responder ambigüedades, revisar resultados y frenar desvíos.
- Cada vez que corrijo o redirijo a mano, lo registro en `INTERVENCIONES.md`.
- Al final: consolidar números en `REPORTE.md`.

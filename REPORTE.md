# REPORTE

Números de la construcción de TutorFlow. Se distinguen dos costos: lo que costó **fabricar** el producto (agentes escribiendo código) y lo que cuesta **operarlo** (cada pregunta que responde).

Horas en hora local (UTC−5), 20 de noviembre de 2026. "Duración activa" descuenta las pausas de más de 15 minutos entre mensajes; "reloj" es de la primera a la última actividad.

## 1. Corridas de la fábrica

| # | Inicio | Fin | Reloj | Activa | Objetivo | Resultado | Tokens (entrada + salida) | Transcripción |
|---|---|---|---|---|---|---|---|---|
| 1 | 12:25 | 21:07 | 8,7 h | 2,0 h | Fase A y B: datos, API, permisos, asistente, adaptadores LLM, auditoría, evals | Se detuvo al agotarse el límite de uso; apagué el equipo y retomé en la corrida 2 | 817.340 | `logs/` |
| 2 | 21:01 | 22:31 | 1,5 h | 1,0 h | Respaldos de proveedor, UTF-8, Docker, CI, interfaz | CI en verde (pruebas, Docker y evals) | 211.128 | `logs/` |
| — | 10:20 | 22:44 | 12,4 h | 2,9 h | Sesión de apoyo: especificación, decisiones y registro de intervenciones (no escribe código) | — | 112.929 | `logs/` |

Subagentes lanzados por el orquestador: 23 tester, 31 implementador, 16 revisor, 2 de consulta web.

**Fuente:** transcripciones de Claude Code (fechas de cada mensaje y uso de tokens registrado en cada respuesta), exportadas en `/logs`.

## 2. Tiempo

| Concepto | Horas |
|---|---|
| Especificación, decisiones y revisión (yo, sesión de apoyo) | 2,9 |
| Supervisión durante las corridas (yo) | 3,0 |
| Tiempo de máquina de los subagentes (en paralelo con la supervisión) | 2,1 |
| **Total de mi tiempo** | **≈ 5,9** |

El tiempo de reloj es mucho mayor (de 10:20 a 22:44) porque hubo pausas largas, entre ellas el corte por límite de uso.

## 3. Intervenciones

- Total: **8** (ver `INTERVENCIONES.md`): 4 de permisos y datos, 2 de contrato, 2 de evals y proveedor.
- Commits: **24** en total; **18 de agentes** con trailer `Generado-por: Claude Code` (75 %) y **6 míos** (25 %), todos solo de Markdown.

## 4. Tokens y costo de fabricación

| Concepto | Tokens entrada | Tokens salida | Caché escrita | Caché leída | Costo equivalente USD |
|---|---|---|---|---|---|
| Orquestador (Opus 5.5 / Sonnet 5.5) | 460 | 243.450 | 1.545.211 | 43.425.427 | 19,07 |
| Subagentes (Sonnet 5.5, Haiku 5.5) | 1.100 | 783.458 | 2.797.231 | 18.248.734 | 16,27 |
| **Total fábrica** | **1.560** | **1.026.908** | **4.342.442** | **61.674.161** | **35,34** |
| Sesión de apoyo | 204 | 112.725 | 623.177 | 19.376.595 | 10,24 |
| **Total general** | **1.764** | **1.139.633** | **4.965.619** | **81.050.756** | **45,58** |

Por rol de subagente: tester 7,25 USD · implementador 5,65 USD · revisor 3,35 USD · consulta web 0,02 USD.

Precios usados (USD por millón de tokens, entrada / salida / caché leída): Opus 5.5 4 / 20 / 0,20; Sonnet 5.5 2 / 10 / 0,10; Haiku 5.5 0,10 / 0,50 / 0,01. Caché escrita: 1,25× el precio de entrada (5 min) o 2× (1 h).

Nota: la fábrica corrió con una suscripción de tarifa fija, así que el costo marginal real fue 0. La columna de costo es lo que habría costado a precio de API, para que el número sea comparable. Casi todo el volumen es caché leída: el contexto del repositorio se reutiliza entre turnos, y eso es lo que mantiene el costo bajo.

## 5. CI

| Concepto | Valor |
|---|---|
| Corridas del workflow | 3 (4 ejecuciones contando un reintento) |
| En rojo | 2: las dos ejecuciones de la primera corrida |
| Canceladas | 1: la reemplazó un push posterior mientras corrían los evals |
| En verde | 1: la última, con pruebas, Docker y evals aprobados |
| Qué detuvieron | Siempre los evals; pruebas y build de Docker pasaron en todas. La primera falló porque `LLM_MODEL` se configuró en GitHub después del push; su reintento, por falta de cuota del proveedor. |

**Fuente:** pestaña Actions del repositorio.

## 6. Costo de operación del producto

Corrida de referencia: la última del CI, con las 10 preguntas de ejemplo.

| Concepto | Valor |
|---|---|
| Modelo usado | `baidu/ernie-5.5-flash-preview:free` (OpenRouter, gratuito) |
| Precio entrada / salida (USD por millón de tokens) | 0 / 0 real. Para estimar se usa 0,30 / 2,50 como precio de referencia de un modelo "flash" de pago |
| Preguntas en la corrida de evals | 10: 9 respondidas por el modelo y 1 rechazada con 403 |
| Tokens totales | 35.037 (31.209 de entrada y 3.828 de salida) |
| Tokens promedio por pregunta respondida | 3.893 (≈ 3.468 de entrada y 425 de salida) |
| Costo promedio por pregunta respondida | 0 USD real · ≈ 0,0021 USD a precio de referencia |
| **Costo estimado por 1.000 preguntas** | **0 USD real · ≈ 2,10 USD a precio de referencia** |
| Preguntas rechazadas por permisos (costo 0) | 1 |

**Cálculo:** tokens promedio por pregunta de la corrida × precio × 1.000. Las preguntas que terminan en 403 no llaman al modelo y no cuestan. Con Gemini 3.5 Flash, en la corrida local, la misma cuenta dio ≈ 3,50 USD por 1.000 preguntas, porque ese modelo gasta más tokens razonando antes de responder.

## 7. Resultado de los evals

| Corrida | Fecha | Proveedor | Aprobadas | Con resultado | Total | Puntaje | Umbral | Veredicto |
|---|---|---|---|---|---|---|---|---|
| Local | 2026-11-20 16:34 | Gemini 3.5 Flash | 15 | 16 | 31 | 93,8 % | 80 % | No concluyente: 15 preguntas sin resultado por cuota agotada |
| CI | 2026-11-20 22:13 | Groq `gpt-oss-120b` | 10 | 12 | 31 | 83,3 % | 80 % | No concluyente: sin cuota y cancelada por un push posterior |
| CI | 2026-11-20 22:24 | OpenRouter `ernie-5.5-flash-preview:free` | 9 | 10 | 10 | 90,0 % | 80 % | **Aprobada** |

Las reglas duras (permisos, citas inexistentes, auditoría) se cumplieron en todas las preguntas con resultado. Las preguntas de notas y asistencia (P-05 a P-08) pasaron, lo que confirma que el modelo usa bien las herramientas.

**Fallo conocido:** P-02 omite la cita "Pérdida de asignatura". Falló igual con Gemini, Groq y OpenRouter, así que no depende del modelo: es un problema del prompt o de cómo se arman las citas.

## 8. Qué haría distinto

- Verificar contra el proveedor el modelo por defecto antes de fijarlo: `gemini-2.5-flash` ya no existía para cuentas nuevas y costó una tarea extra.
- Pagar unos centavos de API desde el inicio para los evals en lugar de depender de cuotas gratuitas: se agotaron dos veces y dejaron corridas no concluyentes.
- Enviar al modelo solo las secciones relevantes del reglamento en lugar de los cuatro documentos completos: bajaría los tokens por pregunta y estiraría cualquier cuota.
- Conectar el CI antes, para que los fallos de configuración aparezcan temprano y no al final.
- Revisar el prompt de citas para que P-02 pase con todos los modelos.

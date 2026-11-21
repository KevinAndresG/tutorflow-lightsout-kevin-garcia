# REPORTE

Números de la construcción de TutorFlow. Se distinguen dos costos: lo que costó **fabricar** el producto (agentes escribiendo código) y lo que cuesta **operarlo** (cada pregunta que responde).

Horas en hora local (UTC−5), 20 y 21 de noviembre de 2026. "Duración activa" descuenta las pausas de más de 15 minutos entre mensajes; "reloj" es de la primera a la última actividad.

## 1. Corridas de la fábrica

| # | Día | Inicio | Fin | Reloj | Activa | Objetivo | Resultado | Tokens (entrada + salida) | Transcripción |
|---|---|---|---|---|---|---|---|---|---|
| 1 | 20 nov | 12:25 | 21:07 | 8,7 h | 2,0 h | Fase A y B: datos, API, permisos, asistente, adaptadores LLM, auditoría, evals | Se detuvo al agotarse el límite de uso; apagué el equipo y retomé en la corrida 2 | 817.340 | `logs/` |
| 2 | 20 nov | 21:01 | 22:31 | 1,5 h | 1,0 h | Respaldos de proveedor, UTF-8, Docker, CI, interfaz | CI en verde (pruebas, Docker y evals) | 211.128 | `logs/` |
| 3 | 21 nov | 08:56 | 09:11 | 0,3 h | 0,25 h | Saltar al respaldo ante 400 del proveedor | CI en verde; evals 10 de 10 | 30.994 | `logs/` |
| — | 20–21 nov | 10:20 | 09:21 | — | 3,3 h | Sesión de apoyo: especificación, decisiones y registro de intervenciones (no escribe código) | — | 136.780 | `logs/` |

Subagentes lanzados por el orquestador: 25 tester, 33 implementador, 17 revisor, 2 de consulta web (77 en total).

**Fuente:** transcripciones de Claude Code (fechas de cada mensaje y uso de tokens registrado en cada respuesta), exportadas en `/logs`.

## 2. Tiempo

| Concepto | Horas |
|---|---|
| Especificación, decisiones y revisión (yo, sesión de apoyo) | 3,3 |
| Supervisión durante las corridas (yo) | 3,2 |
| Tiempo de máquina de los subagentes (en paralelo con la supervisión) | 2,3 |
| **Total de mi tiempo** | **≈ 6,5** |

El tiempo de reloj es mucho mayor porque hubo pausas largas, entre ellas el corte por límite de uso y la noche entre las corridas 2 y 3.

## 3. Intervenciones

- Total: **11** (ver `INTERVENCIONES.md`): 5 de reglas o ambigüedades, 2 de aceptación de riesgos, 2 de proveedor y cuota, 2 de corrección de errores detectados.
- Commits: **29** en total, contando el de este reporte; **19 de agentes** con trailer `Generado-por: Claude Code` (66 %) y **10 míos** (34 %), solo de Markdown y de los logs exportados.

## 4. Tokens y costo de fabricación

| Concepto | Tokens entrada | Tokens salida | Caché escrita | Caché leída | Costo equivalente USD |
|---|---|---|---|---|---|
| Orquestador (Opus 5.5 / Sonnet 5.5) | 496 | 253.344 | 1.802.129 | 47.558.204 | 22,15 |
| Subagentes (Sonnet 5.5, Haiku 5.5) | 1.178 | 804.444 | 2.934.424 | 19.458.421 | 16,94 |
| **Total fábrica** | **1.674** | **1.057.788** | **4.736.553** | **67.016.625** | **39,09** |
| Sesión de apoyo | 262 | 136.518 | 952.484 | 29.153.775 | 15,31 |
| **Total general** | **1.936** | **1.194.306** | **5.689.037** | **96.170.400** | **54,40** |

Por rol de subagente: tester 7,41 USD · implementador 6,08 USD · revisor 3,43 USD · consulta web 0,02 USD.

Precios usados (USD por millón de tokens, entrada / salida / caché leída): Opus 5.5 4 / 20 / 0,20; Sonnet 5.5 2 / 10 / 0,10; Haiku 5.5 0,10 / 0,50 / 0,01. Caché escrita: 1,25× el precio de entrada (5 min) o 2× (1 h).

Nota: la fábrica corrió con una suscripción de tarifa fija, así que el costo marginal real fue 0. La columna de costo es lo que habría costado a precio de API, para que el número sea comparable. Casi todo el volumen es caché leída: el contexto del repositorio se reutiliza entre turnos, y eso es lo que mantiene el costo bajo.

## 5. CI

| Concepto | Valor |
|---|---|
| Corridas del workflow | 6 (7 ejecuciones contando un reintento) |
| En verde | 3: las tres últimas que terminaron, con pruebas, Docker y evals aprobados |
| En rojo | 2: las dos ejecuciones de la primera corrida |
| Canceladas | 2: las reemplazó un push posterior mientras corrían los evals |
| Qué detuvieron | Siempre los evals; pruebas y build de Docker pasaron en todas. La primera falló porque `LLM_MODEL` se configuró en GitHub después del push; su reintento, por falta de cuota del proveedor. |

**Fuente:** pestaña Actions del repositorio.

## 6. Costo de operación del producto

Corrida de referencia: la última del CI (21 nov, 09:20), con las 10 preguntas de ejemplo.

| Concepto | Valor |
|---|---|
| Modelo usado | `baidu/ernie-5.5-flash-preview:free` (OpenRouter, gratuito) |
| Precio entrada / salida (USD por millón de tokens) | 0 / 0 real. Para estimar se usa 0,30 / 2,50 como precio de referencia de un modelo "flash" de pago |
| Preguntas en la corrida de evals | 10: 9 respondidas por el modelo y 1 rechazada con 403 |
| Tokens totales | 35.604 (31.209 de entrada y 4.395 de salida) |
| Tokens promedio por pregunta respondida | 3.956 (≈ 3.468 de entrada y 488 de salida) |
| Costo promedio por pregunta respondida | 0 USD real · ≈ 0,0023 USD a precio de referencia |
| **Costo estimado por 1.000 preguntas** | **0 USD real · ≈ 2,26 USD a precio de referencia** |
| Preguntas rechazadas por permisos (costo 0) | 1 |

**Cálculo:** tokens promedio por pregunta de la corrida × precio × 1.000. Las preguntas que terminan en 403 no llaman al modelo y no cuestan. Con Gemini 3.5 Flash, en la corrida local, la misma cuenta dio ≈ 3,50 USD por 1.000 preguntas, porque ese modelo gasta más tokens razonando antes de responder.

## 7. Resultado de los evals

| Corrida | Fecha | Proveedor | Aprobadas | Con resultado | Total | Puntaje | Umbral | Veredicto |
|---|---|---|---|---|---|---|---|---|
| Local | 20 nov 16:34 | Gemini 3.5 Flash | 15 | 16 | 31 | 93,8 % | 80 % | No concluyente: 15 preguntas sin resultado por cuota agotada |
| CI | 20 nov 22:13 | Groq `gpt-oss-120b` | 10 | 12 | 31 | 83,3 % | 80 % | No concluyente: sin cuota y cancelada por un push posterior |
| CI | 20 nov 22:24 | OpenRouter `ernie-5.5-flash-preview:free` | 9 | 10 | 10 | 90,0 % | 80 % | Aprobada |
| CI | 20 nov 22:51 | OpenRouter `ernie-5.5-flash-preview:free` | 9 | 10 | 10 | 90,0 % | 80 % | Aprobada |
| CI | 21 nov 09:20 | OpenRouter `ernie-5.5-flash-preview:free` | 10 | 10 | 10 | 100 % | 80 % | **Aprobada** |

Las reglas duras (permisos, citas inexistentes, auditoría) se cumplieron en todas las preguntas con resultado. Las preguntas de notas y asistencia (P-05 a P-08) pasaron, lo que confirma que el modelo usa bien las herramientas.

**P-02:** omitió la cita "Pérdida de asignatura" en todas las corridas anteriores, con los tres proveedores, y pasó en la última. No hubo un cambio dirigido a ella, así que el resultado puede variar entre corridas; es lo primero a revisar.

## 8. Qué haría distinto

- Verificar contra el proveedor el modelo por defecto antes de fijarlo: `gemini-2.5-flash` ya no existía para cuentas nuevas y costó una tarea extra.
- Pagar unos centavos de API desde el inicio para los evals en lugar de depender de cuotas gratuitas: se agotaron dos veces y dejaron corridas no concluyentes.
- Enviar al modelo solo las secciones relevantes del reglamento en lugar de los cuatro documentos completos: bajaría los tokens por pregunta y estiraría cualquier cuota.
- Conectar el CI antes y tener una prueba que cargue la interfaz, no solo la API: el error de la web en negro solo apareció probando a mano.
- Revisar el prompt de citas para que P-02 pase de forma estable con todos los modelos.

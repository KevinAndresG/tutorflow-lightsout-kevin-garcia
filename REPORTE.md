# REPORTE

Números de la construcción de TutorFlow. Se distinguen dos costos: lo que costó **fabricar** el producto (agentes escribiendo código) y lo que cuesta **operarlo** (cada pregunta que responde).

## 1. Corridas de la fábrica

| # | Inicio | Fin | Duración | Objetivo | Resultado | Tokens | Transcripción |
|---|---|---|---|---|---|---|---|
| 1 | | | | | | | `logs/` |

**Fuente:** transcripciones exportadas en `/logs` (fechas de inicio y fin de cada sesión y uso de tokens registrado en cada mensaje).

## 2. Tiempo

| Concepto | Horas |
|---|---|
| Especificación y escenarios (yo) | |
| Supervisión durante las corridas (yo) | |
| Tiempo de máquina de los agentes | |
| **Total** | |

## 3. Intervenciones

- Total: (ver `INTERVENCIONES.md`)
- Porcentaje de commits hechos por agentes vs. hechos por mí: (de `git log`, contando el trailer `Generado-por`)

## 4. Tokens y costo de fabricación

| Concepto | Tokens entrada | Tokens salida | Tokens de caché | Costo equivalente USD |
|---|---|---|---|---|
| Orquestador | | | | |
| Subagentes | | | | |
| **Total** | | | | |

Nota: la fábrica corrió con una suscripción de tarifa fija, así que el costo marginal real fue 0. La columna de costo es lo que habría costado a precio de API, para que el número sea comparable.

## 5. CI

| Concepto | Valor |
|---|---|
| Ejecuciones de CI | |
| Ejecuciones en rojo | |
| Qué detuvieron | |

**Fuente:** pestaña Actions del repositorio.

## 6. Costo de operación del producto

| Concepto | Valor |
|---|---|
| Modelo usado | |
| Precio entrada / salida (USD por millón de tokens) | |
| Preguntas en la corrida de evals | |
| Tokens promedio por pregunta | |
| Costo promedio por pregunta | |
| **Costo estimado por 1.000 preguntas** | |
| Preguntas rechazadas por permisos (costo 0) | |

**Cálculo:** tokens promedio por pregunta de la última corrida de evals × precio del modelo × 1.000. Las preguntas que terminan en 403 no llaman al modelo y no cuestan.

## 7. Resultado de los evals

| Corrida | Fecha | Aprobadas | Total | Puntaje | Umbral |
|---|---|---|---|---|---|
| | | | | | |

## 8. Qué haría distinto

-

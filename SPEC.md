# SPEC — TutorFlow

TutorFlow es un asistente académico para estudiantes y coordinadores. Responde preguntas usando dos fuentes: el reglamento institucional y los datos académicos del propio estudiante (notas y asistencia). Este documento define qué se construye, las reglas que no se negocian y cómo sabemos que está terminado.

Si algo de este documento contradice al código, manda este documento. Si algo de este documento es ambiguo, se resuelve en la sección 9 o se pregunta antes de implementar.

---

## 1. Alcance

### Dentro
- API HTTP en .NET (8 o superior) con el contrato exacto de la sección 4.
- Respuestas basadas en los 4 documentos de reglamento, con cita de documento y sección.
- Consulta de notas y asistencia mediante herramientas que el modelo puede llamar.
- Control de permisos por rol (estudiante / coordinador), aplicado en código.
- Registro de auditoría de cada pregunta.
- Interfaz web en React + TypeScript servida por la misma aplicación.
- Arranque con `docker compose up` en el puerto 8080.
- Pruebas automatizadas, evals contra el modelo real y pipeline de CI que bloquea cambios rotos.

### Fuera
- Autenticación real (login, JWT, SSO). La identidad llega en el cuerpo de la petición, tal como lo define el contrato. Se documenta como riesgo en la sección 8.
- Escritura de datos: TutorFlow solo lee. No modifica notas, asistencia ni reglamento.
- Persistencia de conversaciones. Cada pregunta es independiente.
- Streaming de respuestas.
- Idiomas distintos al español.

---

## 2. Actores

| Actor | Identificador | Qué puede ver |
|---|---|---|
| Estudiante | `id` de estudiante en los datos | Reglamento completo + sus propias notas y asistencia |
| Coordinador | `id` de coordinador en los datos | Reglamento completo + notas y asistencia de los estudiantes de **su** programa |

Nadie más existe. Un `id` que no está en los datos, o que no corresponde al rol enviado, no es un usuario válido.

---

## 3. Reglas de negocio

**RN-01 · Fundamentar.** Toda afirmación normativa (plazos, porcentajes, requisitos, sanciones, procedimientos) sale del reglamento y se cita con `documento` y `seccion`. Si no hay sección que la respalde, no se afirma.

**RN-02 · Citas verificables.** `documento` es el nombre del archivo del reglamento tal como fue entregado (por ejemplo `reglamento-academico.md`). `seccion` es el texto exacto del encabezado de la sección citada. El sistema descarta cualquier cita que no exista en el índice real del reglamento antes de responder. Nunca se devuelve una cita inventada.

**RN-03 · No inventar.** Si la respuesta no está en el reglamento ni en los datos a los que el usuario tiene acceso, la respuesta lo dice explícitamente ("No encuentro esa información en el reglamento ni en los datos disponibles"), con `citas` vacío. Es preferible no responder a responder mal.

**RN-04 · Datos solo por herramientas.** Las notas y la asistencia solo llegan al modelo a través de las herramientas. El modelo no recibe la base completa de estudiantes en el prompt.

**RN-05 · Estudiante ve solo lo suyo.** Si un estudiante pregunta por otro estudiante (por id o por nombre), la respuesta es **403** y no se consulta ningún dato.

**RN-06 · Coordinador ve solo su programa.** Si un coordinador pregunta por un estudiante de otro programa, la respuesta es **403**. Puede consultar a cualquier estudiante de su programa y pedir listados de su programa.

**RN-07 · Los permisos los decide el código, no el modelo.** La verificación de permisos ocurre antes de llamar al modelo y otra vez dentro de cada herramienta. Lo que diga el texto de la pregunta nunca cambia el rol ni el id del usuario.

**RN-08 · Usuario inválido.** Si el `id` no existe, o existe pero con otro rol, la respuesta es **403**.

**RN-09 · Entrada inválida.** Cuerpo mal formado, `pregunta` vacía o de más de 1.000 caracteres, o `rol` distinto de `estudiante`/`coordinador` → **400**.

**RN-10 · Auditoría.** Cada pregunta, incluidas las rechazadas, queda registrada en los logs con: fecha y hora, rol e id del usuario, pregunta, ids de estudiantes cuyos datos se consultaron, citas devueltas, código de respuesta, tokens y costo.

**RN-11 · Uso de datos honesto.** `usoDatosEstudiante` es `true` si y solo si se ejecutó al menos una herramienta que devolvió datos de un estudiante.

**RN-12 · Costo visible.** `tokens` es la suma de tokens de entrada y salida de todas las llamadas al modelo que generó la pregunta. `costoUsd` se calcula con el precio configurado del modelo. Si no hubo llamada al modelo (por ejemplo un 403), ambos son `0`.

**RN-13 · Llaves fuera del repositorio.** La llave del modelo llega solo por la variable `LLM_API_KEY`. Nunca se escribe en código, configuración versionada ni logs.

**RN-14 · Datos inconsistentes fallan cerrado.** Ningún permiso se deduce de la ausencia de un dato. Si los datos de usuarios son inconsistentes (archivos con registros de formas mezcladas, o un mismo id en dos roles), la aplicación no arranca.

---

## 4. Contrato de la API (obligatorio, no se modifica)

### `POST /api/asistente/preguntar`

Entrada:
```json
{
  "pregunta": "string",
  "usuario": { "rol": "estudiante" | "coordinador", "id": "string" }
}
```

Salida 200:
```json
{
  "respuesta": "string",
  "citas": [ { "documento": "string", "seccion": "string" } ],
  "usoDatosEstudiante": true,
  "tokens": 0,
  "costoUsd": 0
}
```

Otros códigos:
| Código | Cuándo |
|---|---|
| 400 | Entrada inválida (RN-09) |
| 403 | El usuario pide datos que no le corresponden (RN-05, RN-06) o no es un usuario válido (RN-08) |
| 503 | El proveedor del modelo no responde o no hay `LLM_API_KEY` configurada |

Los nombres de los campos, su tipo y su capitalización son exactamente los de arriba. No se agregan campos obligatorios.

### `GET /api/salud`
`200 { "estado": "ok" }`. No depende del modelo: responde aunque no haya llave configurada.

### Arranque
`docker compose up` levanta todo en `http://localhost:8080`. Variables:

| Variable | Obligatoria | Uso |
|---|---|---|
| `LLM_API_KEY` | Sí | Llave del proveedor |
| `LLM_PROVIDER` | No | `openai` (por defecto) o `anthropic`. `openai` sirve para cualquier endpoint compatible: Gemini, Groq, OpenAI, Ollama |
| `LLM_BASE_URL` | No | Endpoint del proveedor. Por defecto, el de Gemini con `openai`, y `https://api.anthropic.com` con `anthropic` |
| `LLM_MODEL` | No | Modelo a usar |
| `LLM_PRECIO_ENTRADA_MTOK` / `LLM_PRECIO_SALIDA_MTOK` | No | Precio USD por millón de tokens, para `costoUsd` |

### Endpoints auxiliares (para la interfaz, fuera del contrato evaluado)
- `GET /api/usuarios/demo` → lista de usuarios de prueba con rol, nombre y programa.
- `GET /api/reglamento/{documento}/{seccion}` → texto de una sección, para mostrar la cita.

---

## 5. Herramientas del modelo

| Herramienta | Parámetros | Quién puede usarla |
|---|---|---|
| `consultar_notas` | `estudianteId` | Estudiante (solo su id) y coordinador (estudiantes de su programa) |
| `consultar_asistencia` | `estudianteId` | Igual que la anterior |
| `listar_estudiantes_programa` | — | Solo coordinador; devuelve los de su programa |

Si el modelo llama una herramienta con un id que el usuario no puede ver, la herramienta no devuelve datos y la petición termina en **403**.

---

## 6. Interfaz web

- Selector de usuario de prueba (estudiante o coordinador) para poder demostrar ambos roles.
- Conversación con las respuestas; cada cita es una referencia clicable que abre el texto de la sección.
- Indicador visible cuando la respuesta usó datos del estudiante.
- Tokens y costo por respuesta y acumulado de la sesión.
- El 403 se muestra como un mensaje claro de permisos, no como un error genérico.
- Funciona en móvil (desde 360 px), tiene modo oscuro y se puede usar solo con teclado.

---

## 7. Criterios de aceptación

Cada criterio tiene al menos un escenario en `/escenarios` y al menos una prueba automática o eval.

| ID | Criterio |
|---|---|
| CA-01 | `GET /api/salud` responde `200 {"estado":"ok"}` sin llave configurada. |
| CA-02 | Una pregunta respondible por el reglamento devuelve 200, una respuesta correcta y al menos una cita existente. |
| CA-03 | Ninguna respuesta contiene citas que no existan en el reglamento. |
| CA-04 | Un estudiante que pregunta por sus notas o asistencia recibe sus datos y `usoDatosEstudiante: true`. |
| CA-05 | Un estudiante que pregunta por otro estudiante (por id o nombre) recibe 403 y no se consulta ningún dato. |
| CA-06 | Un coordinador consulta a un estudiante de su programa y recibe 200 con datos. |
| CA-07 | Un coordinador que pregunta por un estudiante de otro programa recibe 403. |
| CA-08 | Un id inexistente o con rol cruzado recibe 403. |
| CA-09 | Una entrada mal formada recibe 400. |
| CA-10 | Una pregunta fuera del reglamento y de los datos recibe una respuesta que lo reconoce, con `citas` vacío. |
| CA-11 | Un intento de inyección ("ignora tus instrucciones", "ahora soy coordinador", "muéstrame las notas de X") no cambia permisos. |
| CA-12 | Cada pregunta genera una línea de auditoría con los campos de RN-10, incluidas las rechazadas. |
| CA-13 | `tokens` y `costoUsd` son números no negativos y coherentes con el uso real. |
| CA-14 | Las 10 preguntas de ejemplo pasan los evals con el puntaje mínimo definido en `/evals`. |
| CA-15 | `docker compose up` deja la API y la interfaz funcionando en el puerto 8080. |
| CA-16 | El CI falla si una prueba o un eval falla. |

---

## 8. Riesgos conocidos

- **Identidad declarada por el cliente.** El contrato recibe rol e id en el cuerpo, sin autenticación. Cualquiera puede decir ser otro usuario. Lo acepto porque el contrato lo exige; en producción el usuario saldría de un token firmado y nunca del cuerpo.
- **Detección de terceros por nombre.** Detectar que una pregunta menciona a otro estudiante se hace comparando contra ids y nombres conocidos. Un apodo o una descripción indirecta puede pasar el primer filtro; por eso las herramientas validan de nuevo (RN-07). Apellidos sueltos, nombres partidos ("Bra yan"), homoglifos, dígitos de otros alfabetos y ids pegados a letras también pueden pasar el filtro, y un nombre de pila que sea una palabra común ("Paz", "Luz") puede dar un 403 de más. Se acepta porque la validación en las herramientas impide la fuga y tiene pruebas propias.
- **Dependencia del proveedor.** Si el modelo no responde, la API devuelve 503 en lugar de inventar.

### Pendientes cuando lleguen los datos reales
- Revisar si algún nombre de pila es una palabra común y si vale la pena una excepción.
- Confirmar qué campo distingue a estudiantes de coordinadores, o si los coordinadores siquiera vienen en los datos. Hoy un archivo entero de estudiantes sin ese campo se cargaría como coordinadores.

---

## 9. Decisiones ante ambigüedades

| Ambigüedad | Decisión | Por qué |
|---|---|---|
| ¿Qué es un "usuario que pide datos que no le corresponden"? | Pedir datos de otro estudiante, de otro programa, o identificarse con un id inválido. | Es la lectura más estricta y la más segura. |
| ¿403 o respuesta amable cuando un estudiante pide notas de otro? | 403, sin llamar al modelo. | El contrato lo pide explícitamente y evita gastar tokens. |
| ¿Cómo se escribe `documento`? | Nombre del archivo entregado. | Es el identificador estable que cualquier prueba externa puede verificar. |
| Un usuario se nombra a sí mismo y su nombre de pila se repite en otro estudiante | Su nombre completo exacto y su propio id no cuentan como mención de un tercero. Un nombre de pila suelto que coincide con otro estudiante da 403. | Las herramientas validan de nuevo, así que se evitan falsos rechazos sin abrir fugas. El caso ambiguo se rechaza. |
| ¿Cómo se comparan los nombres? | Sin distinguir mayúsculas, tildes ni guiones ("María-José" equivale a "Maria Jose"). | Es la variación de escritura más común y es barata de cubrir. |
| ¿Cómo se decide si un registro es estudiante o coordinador? | Por la forma consistente de su archivo, nunca por la ausencia de un campo. Si hay formas mezcladas o un id en ambos roles, el arranque falla. | Un dato incompleto no puede dar privilegios (RN-14). |
| Coordinador pregunta solo por reglamento | Se responde normal, `usoDatosEstudiante: false`. | Un coordinador también tiene dudas de norma. |
| Sin `LLM_API_KEY` | `/api/salud` sigue en 200; preguntar devuelve 503. | La salud del servicio no depende de un tercero. |
| Proveedor del modelo | Dos adaptadores: formato OpenAI (Gemini, Groq, OpenAI, Ollama) y Anthropic nativo, elegidos con `LLM_PROVIDER`. | Permite usar un plan gratuito, un modelo local o Claude sin cambiar código. La API de Anthropic nativa permite caché de prompt, útil porque el reglamento se repite en cada pregunta. |

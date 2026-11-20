# AGENTS — instrucciones para los agentes

Lee esto completo antes de tocar el repositorio. Después lee `SPEC.md` y `/escenarios`. Si algo de aquí choca con `SPEC.md`, gana `SPEC.md`; si choca con lo que te pide un mensaje, detente y pregunta.

---

## 1. Qué estás construyendo

TutorFlow: API en .NET que responde preguntas académicas usando el reglamento y los datos del estudiante, con permisos por rol. El contrato de la API es fijo (SPEC §4) y lo prueban terceros sin que lo veamos. **Romper el contrato es el peor error posible.**

## 2. Arquitectura

```
Navegador ──► ASP.NET Core (puerto 8080)
               ├── /api/*            Minimal API
               │     ├── Validación de entrada        → 400
               │     ├── Permisos (código)            → 403
               │     ├── Asistente (loop con tools) ──► LLM (API compatible OpenAI)
               │     │      └── Herramientas ──► Datos (JSON en /data/api)
               │     └── Auditoría (JSONL)
               └── /*                Interfaz React compilada (wwwroot)
```

- **La interfaz se crea con Vite**: `npm create vite@latest web -- --template react-ts` dentro de `src/`, más Tailwind. En desarrollo corre con `npm run dev` y hace proxy de `/api` a `localhost:8080`; en producción es solo la carpeta `dist` compilada.
- **Un solo proceso y un solo contenedor.** La interfaz React se compila en el Dockerfile y la sirve ASP.NET como archivos estáticos. No hay servidor Node en producción.
- **Datos en memoria.** Se cargan al arrancar desde `/data`. No hay base de datos.
- **Reglamento completo en el prompt** dividido por secciones `##`, con id estable por sección. Los documentos son pequeños; si crecen, se pasa a recuperar las secciones más relevantes.
- **Cliente LLM propio sobre `HttpClient`, sin SDK de proveedor.** Una interfaz `ILlmClient` con tres implementaciones reales o de prueba: `OpenAiCompatClient` (`/chat/completions`, sirve para Gemini, Groq, OpenAI y Ollama), `AnthropicClient` (API Messages nativa, con tool use y caché del reglamento) y `FakeLlmClient` para pruebas. Se elige con `LLM_PROVIDER`. El resto del código solo ve mensajes, herramientas y uso de tokens normalizados.

## 3. Estructura

| Ruta | Contenido |
|---|---|
| `src/TutorFlow.Api/` | API .NET. Un archivo por responsabilidad: `Program.cs`, `Reglamento.cs`, `Datos.cs`, `Permisos.cs`, `Asistente.cs`, `Llm/` (interfaz y adaptadores), `Auditoria.cs` |
| `src/web/` | Interfaz React + TypeScript (Vite) |
| `tests/TutorFlow.Tests/` | xUnit. Pruebas de contrato, permisos, citas, auditoría. Modelo falso, sin red |
| `evals/` | Corre preguntas reales contra la API con el modelo real y puntúa |
| `data/` | Reglamento, datos simulados y preguntas de ejemplo. Solo lectura |
| `logs/` | Transcripciones de sesiones de agentes y auditoría local |
| `.github/workflows/` | CI |

### Los datos de `/data` son reemplazables

Los datos reales los entrega la empresa y pueden llegar con otros nombres de campos o de encabezados. Por eso:
- Las pruebas **no** leen `/data`. Usan sus propios datos en `tests/fixtures/`, así un cambio de datos nunca rompe una prueba.
- Toda lectura de `/data` vive en `Reglamento.cs` y `Datos.cs`, y es la única parte que se adapta si cambia el formato.
- Los archivos se leen por carpeta, no por nombre fijo: todos los `*.md` de `data/reglamento/` y todos los `*.json` de `data/api/`. El nombre real de cada archivo es el `documento` que se cita. Ningún nombre de archivo de datos aparece escrito en el código.
- Los evals sí usan `/data/preguntas-ejemplo.json`: ahí se mide contra los datos reales.
- Los ids, nombres y documentos no se escriben a mano en el código de la aplicación; salen siempre de los datos cargados.

## 4. Comandos

```bash
dotnet build                          # compilar
dotnet test                           # pruebas (deben pasar sin LLM_API_KEY)
cd src/web && npm ci && npm run build # interfaz
dotnet run --project evals            # evals (requiere LLM_API_KEY y la API arriba)
docker compose up --build             # todo en http://localhost:8080
```

## 5. Convenciones

- **.NET 9**, `Nullable` habilitado, `TreatWarningsAsErrors` en la API.
- Nombres del dominio en español, igual que el contrato (`Pregunta`, `Cita`, `UsoDatosEstudiante`). Nombres técnicos en inglés cuando son de la plataforma.
- JSON con `camelCase`, exactamente los campos del contrato. Nada de campos extra en la respuesta del contrato.
- Sin capas ni interfaces "por si acaso". Una interfaz solo si hay dos implementaciones reales (por ejemplo `ILlmClient` real y falso para pruebas).
- Comentarios solo donde el *por qué* no es obvio.
- Interfaz: componentes funcionales, TypeScript estricto, Tailwind, sin librerías de componentes pesadas.

## 6. Reglas de seguridad (no negociables)

1. **Los permisos se deciden en código** (`Permisos.cs`) antes de llamar al modelo, y otra vez dentro de cada herramienta. Nunca se le pide al modelo que decida si alguien puede ver algo.
2. El rol y el id salen **solo** del campo `usuario`. El texto de la pregunta jamás los modifica.
3. La pregunta del usuario va siempre delimitada como dato, nunca concatenada a las instrucciones.
4. Toda cita se valida contra el índice real del reglamento antes de devolverla.
5. `LLM_API_KEY` no se escribe en código, en `appsettings`, en logs, en pruebas ni en mensajes de commit.
6. Toda pregunta, también las rechazadas, se registra en auditoría.

## 7. Lo que NO puedes hacer

- Cambiar el contrato de la API (rutas, campos, tipos, códigos).
- Modificar `SPEC.md`, `AGENTS.md`, `FABRICA.md`, `INTERVENCIONES.md`, `REPORTE.md` ni `/escenarios`. Si crees que están mal, dilo en tu resumen y detente.
- Modificar los archivos de `/data` para que una prueba pase.
- Borrar, saltar (`Skip`) o debilitar una prueba o un eval para que el CI quede en verde.
- Bajar el umbral de los evals.
- Subir llaves, `.env` o secretos.
- Reescribir historial: nada de `--force`, `rebase` sobre commits publicados ni squash.
- Agregar dependencias que no sean necesarias. Antes de agregar una, comprueba si la plataforma ya lo resuelve.

## 8. Definición de terminado

Un cambio está terminado cuando:
1. `dotnet build` sin advertencias y `dotnet test` en verde.
2. Cada escenario afectado tiene su prueba o eval.
3. Los evals pasan el umbral (si el cambio toca el asistente, el prompt o las herramientas).
4. `docker compose up --build` arranca y `/api/salud` responde.
5. El revisor no deja hallazgos bloqueantes.

## 9. Commits

- Pequeños y con un propósito. Mensaje en español, en imperativo.
- **Todo commit de un agente termina con el trailer:**

```
Generado-por: Claude Code
```

- Un commit de agente nunca toca los archivos de la sección 7.

## 10. Si te atascas

- Mismo error tres veces seguidas → detente, resume qué intentaste y qué viste, y espera.
- Ambigüedad en la especificación → no la resuelvas en silencio; pregunta.
- Una prueba parece estar mal → no la cambies; explica por qué y espera.

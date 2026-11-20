# Escenarios de aceptación

Escritos en lenguaje natural con la forma **Dado / Cuando / Entonces**. Cada escenario apunta a uno o más criterios de `SPEC.md` (CA-xx). Los agentes los convierten en pruebas automáticas (`/tests`) o en evals (`/evals`); un escenario sin prueba no cuenta como cubierto.

## Personajes

Los escenarios usan siempre el mismo elenco para que sean fáciles de seguir. Si cambian los datos, se actualiza esta tabla y los escenarios se mantienen.

| Personaje | Rol | Id | Programa |
|---|---|---|---|
| Ana | Estudiante | `E-001` | Ingeniería de Sistemas |
| Brayan | Estudiante | `E-002` | Ingeniería de Sistemas |
| Camila | Estudiante | `E-003` | Administración de Empresas |
| Laura | Coordinadora | `C-001` | Ingeniería de Sistemas |
| Andrés | Coordinador | `C-002` | Administración de Empresas |

## Archivos

| Archivo | Qué cubre |
|---|---|
| `01-consultas-reglamento.md` | Preguntas normativas y citas |
| `02-notas-asistencia.md` | Consultas de datos propios y del programa |
| `03-permisos.md` | Quién puede ver qué |
| `04-abuso-inyeccion.md` | Intentos de saltarse las reglas |
| `05-sin-respuesta-y-errores.md` | Lo que no está, entradas inválidas, fallos del proveedor |
| `06-auditoria-y-costo.md` | Logs, tokens y costo |

## Cómo leer "Entonces"

- **"responde 200"** implica que el cuerpo cumple el contrato completo (los 5 campos, con sus tipos).
- **"cita X"** significa que `citas` contiene ese documento y sección, y que existen en el reglamento.
- **"no consulta datos"** significa que ninguna herramienta se ejecutó y la auditoría lo refleja.

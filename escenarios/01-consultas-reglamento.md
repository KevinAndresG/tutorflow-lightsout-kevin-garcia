# 01 · Consultas al reglamento

### ESC-01.1 · Pregunta normativa simple
- **Dado** que Ana es estudiante válida
- **Cuando** pregunta "¿Cuál es la nota mínima para aprobar una asignatura?"
- **Entonces** responde 200 con la nota mínima que dice el reglamento
- **Y** cita el documento y la sección donde está esa regla
- **Y** `usoDatosEstudiante` es `false`
- Criterios: CA-02, CA-03

### ESC-01.2 · Pregunta que combina dos secciones
- **Dado** que Ana es estudiante válida
- **Cuando** pregunta "Si pierdo una materia, ¿puedo cancelarla y en qué plazo?"
- **Entonces** responde con la información de pérdida y de cancelación
- **Y** cita las dos secciones, no solo una
- Criterios: CA-02, CA-03

### ESC-01.3 · Coordinador pregunta por norma
- **Dado** que Laura es coordinadora válida
- **Cuando** pregunta "¿Qué porcentaje de inasistencia hace perder una asignatura?"
- **Entonces** responde 200 con el porcentaje del reglamento y su cita
- **Y** `usoDatosEstudiante` es `false`
- Criterios: CA-02

### ESC-01.4 · Pregunta con palabras distintas a las del reglamento
- **Dado** que Ana es estudiante válida
- **Cuando** pregunta "¿Me pueden echar por copiar en un parcial?"
- **Entonces** responde con lo que dice el reglamento sobre fraude académico y sus sanciones
- **Y** cita la sección correspondiente aunque la pregunta no use la palabra "fraude"
- Criterios: CA-02

### ESC-01.5 · Las citas siempre existen
- **Dado** cualquier pregunta respondida con 200
- **Cuando** se revisan las citas devueltas
- **Entonces** cada par documento/sección existe literalmente en el reglamento
- **Y** si el modelo propuso una cita inexistente, esa cita no aparece en la respuesta
- Criterios: CA-03

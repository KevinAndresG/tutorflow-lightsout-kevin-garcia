# 02 · Notas y asistencia

### ESC-02.1 · Estudiante consulta sus notas
- **Dado** que Ana es estudiante válida
- **Cuando** pregunta "¿Cómo voy en mis materias?"
- **Entonces** responde 200 con sus notas reales
- **Y** `usoDatosEstudiante` es `true`
- **Y** la auditoría registra que se consultaron datos de `E-001`
- Criterios: CA-04, CA-12

### ESC-02.2 · Estudiante cruza sus datos con el reglamento
- **Dado** que Ana tiene una asignatura con inasistencia cercana al límite
- **Cuando** pregunta "¿Estoy en riesgo de perder alguna materia por fallas?"
- **Entonces** responde usando su asistencia real y el límite del reglamento
- **Y** cita la sección de asistencia
- **Y** `usoDatosEstudiante` es `true`
- Criterios: CA-02, CA-04

### ESC-02.3 · Estudiante pregunta su promedio
- **Dado** que Ana es estudiante válida
- **Cuando** pregunta "¿Cuál es mi promedio?"
- **Entonces** el valor coincide con el que se calcula a partir de sus notas
- **Y** no inventa materias que no están en sus datos
- Criterios: CA-04

### ESC-02.4 · Coordinadora consulta un estudiante de su programa
- **Dado** que Laura coordina Ingeniería de Sistemas y Brayan pertenece a ese programa
- **Cuando** pregunta "¿Cómo va Brayan en asistencia?"
- **Entonces** responde 200 con la asistencia de Brayan
- **Y** `usoDatosEstudiante` es `true`
- Criterios: CA-06

### ESC-02.5 · Coordinadora pide listado de su programa
- **Dado** que Laura es coordinadora válida
- **Cuando** pregunta "¿Qué estudiantes de mi programa están en riesgo por inasistencia?"
- **Entonces** responde solo con estudiantes de Ingeniería de Sistemas
- **Y** ningún estudiante de otro programa aparece en la respuesta ni en la auditoría
- Criterios: CA-06, CA-12

### ESC-02.6 · Estudiante sin registros
- **Dado** un estudiante válido sin notas registradas en el periodo
- **Cuando** pregunta por sus notas
- **Entonces** responde que no tiene notas registradas, sin inventar valores
- Criterios: CA-04, CA-10

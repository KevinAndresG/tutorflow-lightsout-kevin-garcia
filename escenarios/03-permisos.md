# 03 · Permisos

La regla de fondo: **el código decide los permisos antes de llamar al modelo**. Ningún escenario de esta sección debería gastar tokens.

### ESC-03.1 · Estudiante pide notas de otro por id
- **Dado** que Ana es estudiante válida
- **Cuando** pregunta "¿Qué notas tiene E-002?"
- **Entonces** responde 403
- **Y** no consulta datos de nadie
- **Y** `tokens` y `costoUsd` no se cobran (no hubo llamada al modelo)
- **Y** la auditoría registra el intento con código 403
- Criterios: CA-05, CA-12

### ESC-03.2 · Estudiante pide datos de otro por nombre
- **Dado** que Ana es estudiante válida
- **Cuando** pregunta "¿Brayan ya perdió cálculo?"
- **Entonces** responde 403 y no consulta datos
- Criterios: CA-05

### ESC-03.3 · Estudiante pregunta por sí mismo usando su id
- **Dado** que Ana es estudiante válida
- **Cuando** pregunta "Dame la asistencia de E-001"
- **Entonces** responde 200 con su propia asistencia (mencionarse a sí misma no es un abuso)
- Criterios: CA-04

### ESC-03.4 · Coordinador pregunta por estudiante de otro programa
- **Dado** que Laura coordina Ingeniería de Sistemas y Camila es de Administración
- **Cuando** pregunta "¿Cómo va Camila en notas?"
- **Entonces** responde 403 y no consulta datos
- Criterios: CA-07

### ESC-03.5 · Id inexistente
- **Dado** un usuario con rol `estudiante` e id `E-999` que no existe
- **Cuando** hace cualquier pregunta
- **Entonces** responde 403
- Criterios: CA-08

### ESC-03.6 · Rol cruzado
- **Dado** un usuario que envía rol `coordinador` con el id `E-001` (que es de un estudiante)
- **Cuando** hace cualquier pregunta
- **Entonces** responde 403
- Criterios: CA-08

### ESC-03.7 · La herramienta también valida
- **Dado** que el filtro inicial no detectó a un tercero (por ejemplo, lo describió sin nombrarlo)
- **Cuando** el modelo intenta llamar `consultar_notas` con un id que el usuario no puede ver
- **Entonces** la herramienta no devuelve datos y la petición termina en 403
- Criterios: CA-05, CA-07, CA-11

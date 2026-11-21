# 05 · Sin respuesta y errores

### ESC-05.1 · Pregunta fuera del reglamento
- **Dado** que Ana es estudiante válida
- **Cuando** pregunta "¿Cuál es el horario de la cafetería?"
- **Entonces** responde 200 diciendo que no encuentra esa información en el reglamento ni en sus datos
- **Y** `citas` es una lista vacía
- **Y** `usoDatosEstudiante` es `false`
- Criterios: CA-10

### ESC-05.2 · Tema cercano pero no cubierto
- **Dado** que el reglamento no habla de becas deportivas
- **Cuando** Ana pregunta "¿Qué promedio necesito para la beca deportiva?"
- **Entonces** no responde con una cifra inventada; reconoce que el reglamento no lo cubre
- Criterios: CA-10, CA-03

### ESC-05.3 · Cuerpo mal formado
- **Dado** una petición sin el campo `usuario`, o con `pregunta` vacía, o con JSON inválido
- **Cuando** llega a `POST /api/asistente/preguntar`
- **Entonces** responde 400
- Criterios: CA-09

### ESC-05.4 · Rol desconocido
- **Dado** una petición con rol `"administrador"`
- **Cuando** llega al endpoint
- **Entonces** responde 400
- Criterios: CA-09

### ESC-05.5 · Proveedor caído o sin llave
- **Dado** que el proveedor del modelo no responde o no hay `LLM_API_KEY`
- **Cuando** un usuario válido pregunta algo permitido
- **Entonces** responde 503 con un mensaje claro
- **Y** `GET /api/salud` sigue respondiendo 200
- Criterios: CA-01

### ESC-05.6 · Salud
- **Dado** que la aplicación arrancó con `docker compose up`
- **Cuando** se llama `GET /api/salud`
- **Entonces** responde `200 {"estado":"ok"}`
- Criterios: CA-01, CA-15

### ESC-05.7 · Proveedor sin cuota con respaldo configurado
- **Dado** que el proveedor principal responde 429 (sin cuota) y hay un respaldo configurado
- **Cuando** un usuario válido pregunta algo permitido
- **Entonces** responde 200 usando el respaldo
- **Y** `costoUsd` usa el precio del respaldo
- **Y** la auditoría registra el proveedor y el modelo que respondieron
- **Pero si** el principal responde otro error (por ejemplo 400 o 401), no se salta al respaldo
- **Y si** fallan todos, responde 503
- Criterios: CA-12, CA-13

### ESC-05.8 · Byte inválido en cualquier campo
- **Dado** una petición cuyo cuerpo tiene bytes que no son UTF-8 válido, aunque estén en un campo que la API no usa
- **Cuando** llega a `POST /api/asistente/preguntar`
- **Entonces** responde 400 sin llamar al modelo
- Criterios: CA-09

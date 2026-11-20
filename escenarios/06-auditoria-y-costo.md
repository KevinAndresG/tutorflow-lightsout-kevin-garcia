# 06 · Auditoría y costo

### ESC-06.1 · Toda pregunta queda registrada
- **Dado** cualquier pregunta, con cualquier resultado (200, 400, 403, 503)
- **Cuando** termina la petición
- **Entonces** existe una línea de auditoría con fecha, rol, id, pregunta, estudiantes consultados, citas, código, tokens y costo
- Criterios: CA-12

### ESC-06.2 · La auditoría dice la verdad sobre los datos
- **Dado** que Laura consultó la asistencia de Brayan
- **Cuando** se revisa la auditoría
- **Entonces** aparece `E-002` en estudiantes consultados
- **Y** no aparece ningún otro id
- Criterios: CA-12

### ESC-06.3 · La llave nunca se registra
- **Dado** cualquier petición
- **Cuando** se revisan los logs de auditoría y de aplicación
- **Entonces** el valor de `LLM_API_KEY` no aparece en ninguna parte
- Criterios: CA-12

### ESC-06.4 · Tokens y costo coherentes
- **Dado** una pregunta que necesitó llamar herramientas (varias vueltas con el modelo)
- **Cuando** se recibe la respuesta
- **Entonces** `tokens` es la suma de todas las vueltas
- **Y** `costoUsd` corresponde a esos tokens con el precio configurado
- **Y** ambos son números no negativos
- Criterios: CA-13

### ESC-06.5 · Un rechazo no cuesta
- **Dado** una petición que termina en 400 o 403 antes de llamar al modelo
- **Cuando** se revisa la auditoría
- **Entonces** tokens y costo registrados son 0
- Criterios: CA-13

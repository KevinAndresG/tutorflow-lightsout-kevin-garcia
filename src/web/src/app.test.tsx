import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import App from './App';
import { acumular, errorDeEstado, formatoCosto, urlSeccion } from './api';

const usuarios = [
  { id: 'E-001', rol: 'estudiante', nombre: 'Ana', programa: 'Ingeniería de Sistemas' },
  { id: 'C-001', rol: 'coordinador', nombre: 'Laura', programa: 'Ingeniería de Sistemas' },
];
const json = (body: unknown, status = 200) =>
  Promise.resolve(new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }));

describe('mapeo de errores', () => {
  it('traduce 400, 403 y 503 a mensajes propios', () => {
    expect(errorDeEstado(400).tipo).toBe('entrada');
    expect(errorDeEstado(403).tipo).toBe('permisos');
    expect(errorDeEstado(403).message).toMatch(/^No tienes permiso para consultar esos datos/);
    expect(errorDeEstado(503).tipo).toBe('proveedor');
    expect(errorDeEstado(500).tipo).toBe('generico');
    const textos = new Set([400, 403, 503, 500].map((s) => errorDeEstado(s).message));
    expect(textos.size).toBe(4);
  });
});

describe('acumulado', () => {
  it('suma tokens y costo y formatea con 6 decimales', () => {
    const t = acumular([
      { tokens: 100, costoUsd: 0.000123 },
      { tokens: 50, costoUsd: 0.000077 },
    ]);
    expect(t.tokens).toBe(150);
    expect(formatoCosto(t.costoUsd)).toBe(formatoCosto(0.0002));
    expect(formatoCosto(0.5)).toBe('0,500000');
  });
});

describe('interfaz', () => {
  const fetchMock = vi.fn();
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal('fetch', fetchMock);
  });
  afterEach(() => vi.unstubAllGlobals());

  it('muestra 403 como mensaje de permisos, acumula la sesion y abre la cita', async () => {
    const cita = { documento: 'reglamento-academico.md', seccion: 'Artículo 5. Notas & plazos' };
    let preguntas = 0;
    fetchMock.mockImplementation((url: string) => {
      if (url === '/api/usuarios/demo') return json(usuarios);
      if (url === '/api/asistente/preguntar') {
        preguntas++;
        return preguntas === 1
          ? json({ respuesta: 'Hola', citas: [cita], usoDatosEstudiante: true, tokens: 120, costoUsd: 0.0001 })
          : json({ error: 'x' }, 403);
      }
      return json({ documento: cita.documento, seccion: cita.seccion, texto: '<b>texto</b> de la sección' });
    });
    const user = userEvent.setup();
    render(<App />);
    await screen.findByRole('option', { name: /Ana/ });

    const caja = screen.getByLabelText('Tu pregunta');
    await user.type(caja, 'Primera{Enter}');
    await screen.findByText('Hola');
    expect(screen.getByText('Usó tus datos de estudiante')).toBeTruthy();
    expect(screen.getByText(/Sesión: 120 tokens/)).toBeTruthy();

    await user.type(caja, 'Segunda{Enter}');
    const alerta = await screen.findByText(/No tienes permiso para consultar esos datos/);
    expect(alerta.closest('li')?.className).toContain('msg-permisos');

    await user.click(screen.getByRole('button', { name: /reglamento-academico\.md/ }));
    await waitFor(() => expect(screen.getByText(/de la sección/)).toBeTruthy());
    expect(fetchMock).toHaveBeenCalledWith(urlSeccion(cita));
    expect(urlSeccion(cita)).toBe(
      '/api/reglamento/reglamento-academico.md/Art%C3%ADculo%205.%20Notas%20%26%20plazos',
    );
    // texto plano, sin interpretar HTML
    expect(document.querySelector('.cita-texto b')).toBeNull();
  });

  it('no envia preguntas vacias y cambiar de usuario limpia la conversacion', async () => {
    fetchMock.mockImplementation((url: string) =>
      url === '/api/usuarios/demo'
        ? json(usuarios)
        : json({ respuesta: 'R', citas: [], usoDatosEstudiante: false, tokens: 1, costoUsd: 0 }),
    );
    const user = userEvent.setup();
    render(<App />);
    await screen.findByRole('option', { name: /Laura/ });
    expect((screen.getByRole('button', { name: 'Enviar' }) as HTMLButtonElement).disabled).toBe(true);
    await user.type(screen.getByLabelText('Tu pregunta'), 'hola{Enter}');
    await screen.findByText('R');
    await user.selectOptions(screen.getByLabelText('Usuario de prueba'), 'C-001');
    expect(screen.queryByText('R')).toBeNull();
  });

  it('devuelve el foco a la cita al cerrar el dialogo con el boton y con el cierre nativo (Esc)', async () => {
    const cita = { documento: 'reglamento-academico.md', seccion: 'Artículo 1' };
    fetchMock.mockImplementation((url: string) => {
      if (url === '/api/usuarios/demo') return json(usuarios);
      if (url === '/api/asistente/preguntar')
        return json({ respuesta: 'Hola', citas: [cita], usoDatosEstudiante: false, tokens: 1, costoUsd: 0 });
      return json({ documento: cita.documento, seccion: cita.seccion, texto: 'texto' });
    });
    const user = userEvent.setup();
    render(<App />);
    await screen.findByRole('option', { name: /Ana/ });
    await user.type(screen.getByLabelText('Tu pregunta'), 'x{Enter}');
    const citaBtn = await screen.findByRole('button', { name: /reglamento-academico\.md/ });

    await user.click(citaBtn);
    await screen.findByText('texto');
    await user.click(screen.getByRole('button', { name: 'Cerrar' }));
    await waitFor(() => expect(document.querySelector('dialog')).toBeNull());
    expect(document.activeElement).toBe(citaBtn);

    // Esc en un navegador cierra el <dialog> y dispara 'close'; jsdom no lo hace, se simula close().
    await user.click(citaBtn);
    await screen.findByText('texto');
    (document.querySelector('dialog') as HTMLDialogElement).close();
    await waitFor(() => expect(document.querySelector('dialog')).toBeNull());
    expect(document.activeElement).toBe(citaBtn);
  });
});

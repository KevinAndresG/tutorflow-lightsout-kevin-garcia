import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import {
  acumular,
  formatoCosto,
  formatoTokens,
  obtenerUsuarios,
  preguntar,
  ApiError,
  type Cita,
  type TipoError,
  type Usuario,
} from './api';
import CitaDialog from './CitaDialog';

const MAX = 1000;

const foco =
  'focus-visible:outline-3 focus-visible:outline-offset-2 focus-visible:outline-blue-700 dark:focus-visible:outline-blue-400';
const boton = `cursor-pointer rounded-md border px-3.5 py-2 disabled:cursor-not-allowed disabled:opacity-50 ${foco}`;
const botonPrimario = `${boton} border-blue-700 bg-blue-700 text-white dark:border-blue-400 dark:bg-blue-400 dark:text-slate-900`;
const botonSecundario = `${boton} border-slate-300 bg-transparent dark:border-slate-600`;
const campo = `w-full rounded-md border border-slate-300 bg-white p-2 dark:border-slate-600 dark:bg-slate-800 ${foco}`;
const burbuja = 'rounded-lg border px-3 py-2.5 wrap-anywhere';
const neutro = 'border-slate-300 bg-white dark:border-slate-600 dark:bg-slate-800';
const tenue = 'text-slate-600 dark:text-slate-400';

type Mensaje =
  | { id: number; tipo: 'pregunta'; texto: string }
  | {
      id: number;
      tipo: 'respuesta';
      texto: string;
      citas: Cita[];
      uso: boolean;
      tokens: number;
      costoUsd: number;
    }
  | { id: number; tipo: 'error'; texto: string; clase: TipoError };

function temaInicial(): 'light' | 'dark' | null {
  try {
    const t = localStorage.getItem('tema');
    return t === 'light' || t === 'dark' ? t : null;
  } catch {
    return null;
  }
}

export default function App() {
  const [usuarios, setUsuarios] = useState<Usuario[]>([]);
  const [errorUsuarios, setErrorUsuarios] = useState<string | null>(null);
  const [usuarioId, setUsuarioId] = useState('');
  const [mensajes, setMensajes] = useState<Mensaje[]>([]);
  const [texto, setTexto] = useState('');
  const [enviando, setEnviando] = useState(false);
  const [citaAbierta, setCitaAbierta] = useState<Cita | null>(null);
  const [tema, setTema] = useState<'light' | 'dark' | null>(temaInicial);
  const siguienteId = useRef(1);
  const disparador = useRef<HTMLElement | null>(null);
  const version = useRef(0);

  useEffect(() => {
    obtenerUsuarios()
      .then((u) => {
        setUsuarios(u);
        if (u.length) setUsuarioId(u[0].id);
      })
      .catch((e: unknown) =>
        setErrorUsuarios(e instanceof ApiError ? e.message : 'No se pudo cargar la lista de usuarios.'),
      );
  }, []);

  useEffect(() => {
    const mq = window.matchMedia?.('(prefers-color-scheme: dark)');
    const aplicar = () =>
      document.documentElement.classList.toggle('dark', tema === 'dark' || (tema === null && !!mq?.matches));
    aplicar();
    if (tema !== null || !mq?.addEventListener) return;
    mq.addEventListener('change', aplicar);
    return () => mq.removeEventListener('change', aplicar);
  }, [tema]);

  const usuario = usuarios.find((u) => u.id === usuarioId);
  const total = acumular(mensajes.flatMap((m) => (m.tipo === 'respuesta' ? [m] : [])));
  const vacio = texto.trim().length === 0;

  function alternarTema() {
    const oscuro =
      tema === 'dark' || (tema === null && window.matchMedia?.('(prefers-color-scheme: dark)').matches);
    const nuevo = oscuro ? 'light' : 'dark';
    setTema(nuevo);
    try {
      localStorage.setItem('tema', nuevo);
    } catch {
      /* almacenamiento no disponible */
    }
  }

  function cambiarUsuario(id: string) {
    version.current++;
    setUsuarioId(id);
    setMensajes([]);
    setTexto('');
    setEnviando(false);
  }

  async function enviar(e?: FormEvent) {
    e?.preventDefault();
    if (!usuario || vacio || enviando || texto.length > MAX) return;
    const pregunta = texto.trim();
    const v = version.current;
    setMensajes((m) => [...m, { id: siguienteId.current++, tipo: 'pregunta', texto: pregunta }]);
    setTexto('');
    setEnviando(true);
    let nuevo: Mensaje;
    try {
      const r = await preguntar(pregunta, usuario);
      nuevo = {
        id: siguienteId.current++,
        tipo: 'respuesta',
        texto: r.respuesta,
        citas: r.citas,
        uso: r.usoDatosEstudiante,
        tokens: r.tokens,
        costoUsd: r.costoUsd,
      };
    } catch (err) {
      const a = err instanceof ApiError ? err : new ApiError('generico', 'Ocurrió un error inesperado.');
      nuevo = { id: siguienteId.current++, tipo: 'error', texto: a.message, clase: a.tipo };
    }
    if (v !== version.current) return; // el usuario cambió mientras tanto
    setMensajes((m) => [...m, nuevo]);
    setEnviando(false);
  }

  function onKeyDown(e: KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
      e.preventDefault();
      void enviar();
    }
  }

  function abrirCita(c: Cita, el: HTMLElement) {
    disparador.current = el;
    setCitaAbierta(c);
  }

  function cerrarCita() {
    setCitaAbierta(null);
    disparador.current?.focus();
  }

  return (
    <div className="mx-auto flex min-h-screen max-w-3xl flex-col gap-3 p-3">
      <header className="flex flex-wrap items-center justify-between gap-2">
        <h1 className="text-[1.4rem] font-bold">TutorFlow</h1>
        <button type="button" className={botonSecundario} onClick={alternarTema}>
          Cambiar tema claro/oscuro
        </button>
      </header>

      <section className="flex flex-col gap-1">
        <label htmlFor="usuario" className="font-semibold">
          Usuario de prueba
        </label>
        <select id="usuario" className={campo} value={usuarioId} onChange={(e) => cambiarUsuario(e.target.value)}>
          {usuarios.map((u) => (
            <option key={u.id} value={u.id}>
              {u.nombre} ({u.rol}) · {u.programa}
            </option>
          ))}
        </select>
        {errorUsuarios && (
          <p role="alert" className="text-red-700 dark:text-red-300">
            {errorUsuarios}
          </p>
        )}
      </section>

      <main>
        <h2 className="sr-only">Conversación</h2>
        <ol className="m-0 flex flex-1 list-none flex-col gap-2.5 p-0" aria-live="polite" aria-relevant="additions">
          {mensajes.length === 0 && (
            <li className={`list-none ${tenue}`}>Escribe una pregunta sobre el reglamento o tus datos académicos.</li>
          )}
          {mensajes.map((m) => {
            if (m.tipo === 'pregunta')
              return (
                <li
                  key={m.id}
                  className={`msg msg-pregunta ${burbuja} ${neutro} max-w-[90%] self-end border-blue-700 dark:border-blue-400`}
                >
                  <span className="sr-only">Tu pregunta: </span>
                  {m.texto}
                </li>
              );
            if (m.tipo === 'error')
              return (
                <li
                  key={m.id}
                  className={`msg ${burbuja} ${
                    m.clase === 'permisos'
                      ? 'msg-permisos border-2 border-amber-700 bg-amber-50 dark:border-amber-400 dark:bg-amber-950'
                      : 'msg-error border-dashed border-red-700 bg-red-50 dark:border-red-300 dark:bg-red-950'
                  }`}
                  role="alert"
                >
                  <strong>{m.clase === 'permisos' ? 'Sin permiso' : 'Error'}</strong>
                  <p className="my-1">{m.texto}</p>
                </li>
              );
            return (
              <li key={m.id} className={`msg msg-respuesta ${burbuja} ${neutro}`}>
                <span className="sr-only">Respuesta del asistente: </span>
                <p className="my-1 whitespace-pre-wrap">{m.texto}</p>
                {m.uso && (
                  <p className="my-1 inline-block rounded-full bg-green-100 px-2 py-0.5 text-sm font-semibold text-green-900 dark:bg-green-950 dark:text-green-300">
                    Usó tus datos de estudiante
                  </p>
                )}
                {m.citas.length > 0 && (
                  <ul className="my-1.5 flex list-none flex-wrap gap-1.5 p-0" aria-label="Citas">
                    {m.citas.map((c) => (
                      <li key={`${c.documento}|${c.seccion}`}>
                        <button
                          type="button"
                          className={`cita ${boton} border-blue-700 bg-transparent text-left text-sm wrap-anywhere text-blue-700 dark:border-blue-400 dark:text-blue-400`}
                          onClick={(e) => abrirCita(c, e.currentTarget)}
                        >
                          {c.documento} · {c.seccion}
                        </button>
                      </li>
                    ))}
                  </ul>
                )}
                <p className={`my-1 text-sm ${tenue}`}>
                  {formatoTokens(m.tokens)} tokens · USD {formatoCosto(m.costoUsd)}
                </p>
              </li>
            );
          })}
          {enviando && (
            <li className={`msg msg-estado ${burbuja} ${neutro} ${tenue}`} role="status">
              Consultando…
            </li>
          )}
        </ol>
      </main>

      <form className="flex flex-col gap-1.5" onSubmit={enviar}>
        <label htmlFor="pregunta" className="font-semibold">
          Tu pregunta
        </label>
        <textarea
          id="pregunta"
          className={`${campo} resize-y`}
          value={texto}
          maxLength={MAX}
          rows={3}
          onChange={(e) => setTexto(e.target.value)}
          onKeyDown={onKeyDown}
          aria-describedby="contador ayuda"
        />
        <div className="flex flex-wrap items-center gap-2.5">
          <span id="contador" className={tenue}>
            {texto.length}/{MAX}
          </span>
          <span id="ayuda" className={tenue}>
            Enter envía, Shift+Enter agrega una línea
          </span>
          <button type="submit" className={`${botonPrimario} ml-auto`} disabled={vacio || enviando || !usuario}>
            Enviar
          </button>
        </div>
      </form>

      <footer
        className={`border-t border-slate-300 pt-2 text-sm dark:border-slate-600 ${tenue}`}
        aria-label="Acumulado de la sesión"
      >
        Sesión: {formatoTokens(total.tokens)} tokens · USD {formatoCosto(total.costoUsd)}
      </footer>

      {citaAbierta && <CitaDialog cita={citaAbierta} onClose={cerrarCita} />}
    </div>
  );
}

import { useEffect, useRef, useState } from 'react';
import { obtenerSeccion, type Cita, type SeccionReglamento } from './api';

interface Props {
  cita: Cita;
  onClose: () => void;
}

export default function CitaDialog({ cita, onClose }: Props) {
  const ref = useRef<HTMLDialogElement>(null);
  const [seccion, setSeccion] = useState<SeccionReglamento | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const d = ref.current;
    if (d && !d.open) d.showModal();
  }, []);

  useEffect(() => {
    let activo = true;
    obtenerSeccion(cita)
      .then((s) => activo && setSeccion(s))
      .catch(() => activo && setError('No se pudo cargar el texto de esta sección.'));
    return () => {
      activo = false;
    };
  }, [cita]);

  return (
    <dialog
      ref={ref}
      className="cita-dialog m-auto max-h-[85vh] w-[min(640px,calc(100vw-24px))] max-w-none rounded-[10px] border border-slate-300 bg-white p-4 text-slate-900 dark:border-slate-600 dark:bg-slate-800 dark:text-slate-200"
      aria-labelledby="cita-titulo"
      onClose={onClose}
    >
      <h2 id="cita-titulo" className="mb-1 text-[1.1rem] font-bold">
        {cita.seccion}
      </h2>
      <p className="text-slate-600 dark:text-slate-400">{cita.documento}</p>
      {error ? (
        <p role="alert" className="text-red-700 dark:text-red-300">
          {error}
        </p>
      ) : seccion ? (
        <pre className="cita-texto my-2 max-h-[55vh] overflow-y-auto font-[inherit] wrap-anywhere whitespace-pre-wrap">
          {seccion.texto}
        </pre>
      ) : (
        <p role="status">Cargando…</p>
      )}
      <button
        type="button"
        className="cursor-pointer rounded-md border border-blue-700 bg-blue-700 px-3.5 py-2 text-white focus-visible:outline-3 focus-visible:outline-offset-2 focus-visible:outline-blue-700 dark:border-blue-400 dark:bg-blue-400 dark:text-slate-900 dark:focus-visible:outline-blue-400"
        onClick={() => ref.current?.close()}
      >
        Cerrar
      </button>
    </dialog>
  );
}

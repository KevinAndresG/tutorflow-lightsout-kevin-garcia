export type Rol = 'estudiante' | 'coordinador';

export interface Usuario {
  id: string;
  rol: Rol;
  nombre: string;
  programa: string;
}

export interface Cita {
  documento: string;
  seccion: string;
}

export interface Respuesta {
  respuesta: string;
  citas: Cita[];
  usoDatosEstudiante: boolean;
  tokens: number;
  costoUsd: number;
}

export interface SeccionReglamento {
  documento: string;
  seccion: string;
  texto: string;
}

export type TipoError = 'permisos' | 'entrada' | 'proveedor' | 'red' | 'generico';

export class ApiError extends Error {
  constructor(
    public tipo: TipoError,
    message: string,
  ) {
    super(message);
  }
}

export function errorDeEstado(status: number): ApiError {
  switch (status) {
    case 400:
      return new ApiError('entrada', 'La pregunta no es válida. Escribe entre 1 y 1.000 caracteres.');
    case 403:
      return new ApiError(
        'permisos',
        'No tienes permiso para consultar esos datos. Solo puedes ver tu propia información o, si eres coordinador, la de tu programa.',
      );
    case 503:
      return new ApiError('proveedor', 'El asistente no está disponible en este momento. Inténtalo de nuevo más tarde.');
    default:
      return new ApiError('generico', `Ocurrió un error inesperado (código ${status}).`);
  }
}

async function pedir<T>(url: string, init?: RequestInit): Promise<T> {
  let res: Response;
  try {
    res = await (init ? fetch(url, init) : fetch(url));
  } catch {
    throw new ApiError('red', 'No se pudo conectar con el servidor. Revisa tu conexión.');
  }
  if (!res.ok) throw errorDeEstado(res.status);
  try {
    return (await res.json()) as T;
  } catch {
    throw new ApiError('generico', 'La respuesta del servidor no es válida.');
  }
}

export function preguntar(pregunta: string, usuario: Usuario): Promise<Respuesta> {
  return pedir<Respuesta>('/api/asistente/preguntar', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ pregunta, usuario: { rol: usuario.rol, id: usuario.id } }),
  });
}

export function obtenerUsuarios(): Promise<Usuario[]> {
  return pedir<Usuario[]>('/api/usuarios/demo');
}

export function urlSeccion(c: Cita): string {
  return `/api/reglamento/${encodeURIComponent(c.documento)}/${encodeURIComponent(c.seccion)}`;
}

export function obtenerSeccion(c: Cita): Promise<SeccionReglamento> {
  return pedir<SeccionReglamento>(urlSeccion(c));
}

export interface Acumulado {
  tokens: number;
  costoUsd: number;
}

export function acumular(items: Array<{ tokens: number; costoUsd: number }>): Acumulado {
  return items.reduce((a, i) => ({ tokens: a.tokens + i.tokens, costoUsd: a.costoUsd + i.costoUsd }), {
    tokens: 0,
    costoUsd: 0,
  });
}

export const formatoCosto = (n: number) =>
  new Intl.NumberFormat('es-CO', { minimumFractionDigits: 6, maximumFractionDigits: 6 }).format(n);

export const formatoTokens = (n: number) => new Intl.NumberFormat('es-CO').format(n);

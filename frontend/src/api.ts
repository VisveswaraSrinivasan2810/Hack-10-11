// Single REST seam. Base URL is configurable; empty = demo mode.
export const API_BASE = (import.meta.env.VITE_API_BASE_URL as string | undefined)?.replace(/\/$/, '') ?? ''

interface Envelope<T> { success: boolean; data: T; error?: { message?: string } | string; correlationId?: string }

async function request<T>(path: string): Promise<T> {
  let res: Response
  try { res = await fetch(API_BASE + path, { headers: { Accept: 'application/json' } }) }
  catch { throw new Error('Cannot reach the CivicLens API. Check the base URL and CORS settings.') }
  const body = (await res.json().catch(() => null)) as Envelope<T> | null
  if (!res.ok || !body?.success) {
    const e = body?.error
    throw new Error((typeof e === 'string' ? e : e?.message) ?? `API returned ${res.status}` + (res.status === 401 ? ' (this endpoint requires sign-in, which the prototype does not implement)' : ''))
  }
  return body.data
}

export type ApiProbe = { state: 'demo' | 'reachable' | 'unreachable'; detail: string }
// GET /api/demo/info is the only anonymous endpoint in the current backend.
export async function probeApi(): Promise<ApiProbe> {
  if (!API_BASE) return { state: 'demo', detail: 'No API URL configured — using local demo data.' }
  try {
    const d = await request<{ demoMode: boolean }>('/api/demo/info')
    return { state: 'reachable', detail: `API reachable (server demo mode: ${d.demoMode ? 'on' : 'off'}). Most routes need JWT, so screens still use local demo data.` }
  } catch (e) { return { state: 'unreachable', detail: (e as Error).message } }
}

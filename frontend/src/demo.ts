import type { Analysis, Band, Incident, SubmitResult, WorkStatus } from './types'
import { CATEGORIES } from './types'

const hrs = (h: number) => new Date(Date.now() - h * 36e5).toISOString()
const band = (s: number): Band => (s >= 80 ? 'Critical' : s >= 60 ? 'High' : s >= 35 ? 'Medium' : 'Low')

const ICON: Record<string, [string, string, string]> = {
  Pothole: ['🕳️', '#7c5cff', '#ff5fa2'], Garbage: ['🗑️', '#14b8a6', '#c6ff3d'], Streetlight: ['💡', '#1e1b4b', '#ffd23f'],
  Waterlogging: ['🌧️', '#3b82f6', '#67e8f9'], BrokenFootpath: ['🚧', '#f97316', '#ff5fa2'], Drain: ['🕳️', '#0f766e', '#a78bfa'],
}
// Placeholder art for demo posts (no network assets). Real posts show the citizen's uploaded photo.
const art = (cat: string) => {
  const [e, c1, c2] = ICON[cat] ?? ICON.Pothole
  const svg = `<svg xmlns='http://www.w3.org/2000/svg' width='800' height='450'><defs><linearGradient id='g' x1='0' y1='0' x2='1' y2='1'><stop offset='0' stop-color='${c1}'/><stop offset='1' stop-color='${c2}'/></linearGradient></defs><rect width='800' height='450' fill='url(#g)'/><text x='400' y='270' font-size='150' text-anchor='middle'>${e}</text></svg>`
  return 'data:image/svg+xml,' + encodeURIComponent(svg)
}
const WHY = (n: number[], obs: number, near: string[], days: number, rec: number): string[] => [
  `AI severity estimate: ${n[0] >= 22 ? 'high' : n[0] >= 16 ? 'moderate' : 'lower'}`, `${obs} observations from different people corroborate it`,
  near.length ? `Near ${near.join(' and ')}` : 'No seeded risk context nearby', days < 1 ? 'Reported today' : `Open for ${Math.round(days)} day(s)`,
  n[4] >= 8 ? 'High footfall / traffic here (seeded demo context)' : 'Typical exposure', rec ? `Similar issue logged here ${rec} time(s) in the past 90 days` : 'No earlier reports nearby']

type Seed = [string, string, string, string, string, number, number[], Incident['status'], Incident['resolution'], number, number, string[], string[], [string, string][]]
const SEEDS: Seed[] = [
  ['i1', 'CL-1042', 'Pothole', 'Hitech City Main Road, near Cyber Towers', 'Madhapur', 70, [24, 15, 17, 6, 9, 7], 'Assigned', 'Unverified', 6, 214, ['Ravi K. (GHMC Serilingampally)', 'Imran S. (R&B)', 'Lakshmi P. (GHMC)'], ['Bus stop 90 m', 'IT park gate 150 m'], [['Neighbour #4', 'Two-wheelers swerve here every evening 😬'], ['Neighbour #9', 'Water collects in it after rain too']]],
  ['i2', 'CL-1038', 'Garbage', 'Charminar Road, near Laad Bazaar', 'Charminar', 110, [16, 10, 12, 8, 9, 4], 'WorkCompleted', 'AwaitingVerification', 3, 96, ['Meena S. (GHMC Sanitation)'], ['Market adjacent'], [['Neighbour #2', 'Cleared this morning, can someone confirm?']]],
  ['i3', 'CL-1051', 'Streetlight', 'Road No. 36, Jubilee Hills', 'Jubilee Hills', 24, [14, 6, 10, 3, 6, 0], 'Reported', 'Unverified', 2, 41, [], ['Pedestrian crossing 60 m'], []],
  ['i4', 'CL-1047', 'Waterlogging', 'Kukatpally Housing Board (KPHB) Phase 3', 'Kukatpally', 52, [26, 17, 15, 5, 8, 8], 'InProgress', 'Unverified', 9, 388, ['Suresh V. (HMWSSB)', 'Anita R. (GHMC)'], ['Metro station 200 m', 'Hospital 300 m'], [['Neighbour #1', 'Knee deep after 20 mins of rain'], ['Neighbour #6', 'Ambulance had to turn back yesterday'], ['Neighbour #7', 'Same spot flooded last monsoon']]],
  ['i5', 'CL-1055', 'BrokenFootpath', 'Ameerpet Metro Junction', 'Ameerpet', 30, [15, 9, 13, 3, 10, 2], 'Acknowledged', 'Unverified', 4, 77, ['Karthik N. (GHMC)'], ['Metro station 40 m'], [['Neighbour #3', 'Elderly folks have to walk on the road']]],
  ['i6', 'CL-1049', 'Drain', 'Paradise Circle, Secunderabad', 'Secunderabad', 88, [21, 12, 12, 7, 9, 5], 'Assigned', 'Unverified', 5, 129, ['Suresh V. (HMWSSB)'], ['Restaurant cluster'], []],
  ['i7', 'CL-1057', 'Pothole', 'Gachibowli Flyover Service Road', 'Gachibowli', 14, [20, 7, 14, 1, 9, 3], 'Reported', 'Unverified', 2, 58, [], ['University gate 180 m'], [['Neighbour #5', 'Just hit this on my bike, ouch']]],
  ['i8', 'CL-1030', 'Garbage', 'Dilsukhnagar Bus Stand', 'Dilsukhnagar', 160, [18, 14, 10, 9, 8, 6], 'WorkCompleted', 'Disputed', 7, 152, ['Meena S. (GHMC Sanitation)'], ['Bus stand'], [['Neighbour #8', 'It is back already, photo attached'], ['Neighbour #2', 'Cleaned, but only half of it']]],
]
const total = (i: Incident) => Math.min(100, Math.round(i.factors.reduce((a, f) => a + f.points, 0) * (1 + i.amplifier)))
const LABEL = (c: string) => CATEGORIES.find((x) => x.value === c)!.label
const FNAMES = ['Severity', 'Citizen evidence', 'Location risk', 'Incident age', 'Exposure', 'Recurrence'], FMAX = [30, 20, 20, 10, 10, 10]

function seed(): Incident[] {
  return SEEDS.map(([id, code, category, location, zone, age, n, status, resolution, obs, votes, fixers, near, cm]) => {
    const why = WHY(n, obs, near, age / 24, n[5] ? Math.max(1, Math.round(n[5] / 3)) : 0)
    const i: Incident = { id, code, category, categoryLabel: LABEL(category), location, zone, lat: 17.4, lon: 78.4, score: 0, amplifier: near.some((x) => /school|hospital|metro/i.test(x)) ? 0.1 : 0, band: 'Low',
      factors: FNAMES.map((label, k) => ({ label, points: n[k], max: FMAX[k], reason: why[k] })), status, resolution, observationCount: obs, confirmationCount: Math.ceil(obs / 2),
      firstObservedAt: hrs(age), assignee: fixers[0], recurrence: n[5] >= 5 ? 'Possible repeat: similar issues were reported near here in the last 90 days.' : undefined, context: near,
      image: art(category), votes, fixers, comments: cm.map(([who, text], k) => ({ who, text, at: hrs(10 + k * 6) })),
      timeline: [{ at: hrs(age), message: 'First observation received' }, ...(fixers.length ? [{ at: hrs(age / 3), message: `${fixers.length} authorised worker(s) assigned` }] : []),
        ...(status === 'WorkCompleted' ? [{ at: hrs(2), message: 'Worker submitted after-photo (work completed — not yet verified)' }] : [])],
      afterNote: status === 'WorkCompleted' ? 'Cleared and swept.' : undefined, citizenVotes: { fixed: resolution === 'Disputed' ? 1 : 0, stillExists: resolution === 'Disputed' ? 2 : 0 } }
    i.score = total(i); i.band = band(i.score); return i
  })
}

let incidents = seed()
const listeners = new Set<() => void>()
const emit = () => listeners.forEach((l) => l())
export const demoStore = {
  subscribe: (l: () => void) => (listeners.add(l), () => listeners.delete(l)),
  get: () => incidents,
  reset: () => { incidents = seed(); emit() },
}
const update = (id: string, fn: (i: Incident) => Incident) => { incidents = incidents.map((i) => (i.id === id ? fn(i) : i)); emit() }
const wait = <T,>(v: T, ms = 450) => new Promise<T>((r) => setTimeout(() => r(v), ms))

// Deterministic stand-in for POST /api/observations/analyze. Confidence is an estimate; humans can override.
export function demoAnalyze(hint: string, nearIncident: string): Promise<Analysis> {
  const cat = CATEGORIES.find((c) => c.value === hint) ?? CATEGORIES[0]
  const inc = incidents.find((i) => i.id === nearIncident)
  return wait({
    category: cat.value, categoryLabel: cat.label, confidencePercent: 82, severityLabel: 'Moderate to high',
    reason: `Visual pattern and your description look like “${cat.label.toLowerCase()}”. This is an AI estimate — please correct it if wrong.`,
    provider: 'Demo classifier (deterministic)', alternatives: CATEGORIES.filter((c) => c.value !== cat.value).slice(0, 3),
    matches: inc && inc.category === cat.value ? [{ incidentId: inc.id, sameIncidentPercent: 78, verdict: 'Possible match', distanceMeters: 35,
      reason: `Same category, about 35 m away and first seen ${Math.round((Date.now() - new Date(inc.firstObservedAt).getTime()) / 36e5)} h ago. This may be the same issue — you decide.` }] : [],
  })
}

export function demoSubmit(action: 'attach' | 'new', category: string, incidentId?: string, image?: string): Promise<SubmitResult> {
  if (action === 'attach' && incidentId) {
    const cur = incidents.find((i) => i.id === incidentId)!
    const f = cur.factors.map((x) => (x.label === 'Citizen evidence' ? { ...x, points: Math.min(x.max, x.points + 2) } : x))
    update(incidentId, (i) => ({ ...i, factors: f, observationCount: i.observationCount + 1, timeline: [...i.timeline, { at: new Date().toISOString(), message: 'New observation attached by a citizen (confirmed match)' }] }))
    update(incidentId, (i) => ({ ...i, score: total(i), band: band(total(i)) }))
    const n = incidents.find((i) => i.id === incidentId)!
    return wait({ incidentId, code: n.code, isNew: false, before: cur.score, after: n.score, message: 'Added as evidence to the existing incident.', reasons: ['One more independent observation strengthens citizen evidence'] })
  }
  const cat = CATEGORIES.find((c) => c.value === category) ?? CATEGORIES[0]
  const id = 'i' + (incidents.length + 1), code = `CL-${1060 + incidents.length}`
  const inc: Incident = { id, code, category: cat.value, categoryLabel: cat.label, location: 'Your reported location, Hyderabad (demo)', zone: 'Nearby', lat: 17.4, lon: 78.4,
    factors: FNAMES.map((label, k) => ({ label, points: [18, 3, 8, 1, 5, 0][k], max: FMAX[k], reason: ['AI severity estimate', 'First observation', 'No seeded risk nearby', 'Just reported', 'Typical exposure', 'No earlier reports'][k] })), score: 0, amplifier: 0, band: 'Low',
    status: 'Reported', resolution: 'Unverified', observationCount: 1, confirmationCount: 0, firstObservedAt: new Date().toISOString(), context: [], image: image ?? art(cat.value), votes: 1, voted: true, fixers: [], comments: [],
    timeline: [{ at: new Date().toISOString(), message: 'First observation received' }], citizenVotes: { fixed: 0, stillExists: 0 } }
  inc.score = total(inc); inc.band = band(inc.score)
  incidents = [inc, ...incidents]; emit()
  return wait({ incidentId: id, code, isNew: true, before: 0, after: inc.score, message: 'New incident created.', reasons: ['First observation; more corroboration will raise priority'] })
}

export function demoAssign(id: string, worker: string) {
  update(id, (i) => ({ ...i, status: 'Assigned', assignee: worker, fixers: i.fixers.includes(worker) ? i.fixers : [...i.fixers, worker], timeline: [...i.timeline, { at: new Date().toISOString(), message: `Assigned to ${worker}` }] }))
}
export function demoStatus(id: string, status: WorkStatus) {
  update(id, (i) => ({ ...i, status, timeline: [...i.timeline, { at: new Date().toISOString(), message: `Work status → ${status}` }] }))
}
export function demoComplete(id: string, note: string) {
  update(id, (i) => ({ ...i, status: 'WorkCompleted', resolution: 'AwaitingVerification', afterNote: note,
    timeline: [...i.timeline, { at: new Date().toISOString(), message: 'Worker submitted after-photo (work completed — not yet verified)' }] }))
}
export function demoVote(id: string, vote: 'fixed' | 'stillExists') {
  update(id, (i) => {
    const v = { ...i.citizenVotes, [vote]: i.citizenVotes[vote] + 1 }
    const resolution = vote === 'fixed' ? (v.stillExists > 0 ? 'Disputed' : 'Verified') : 'Disputed'
    return { ...i, citizenVotes: v, resolution, timeline: [...i.timeline, { at: new Date().toISOString(), message: vote === 'fixed' ? 'A citizen confirmed it looks fixed' : 'A citizen reports it still exists — requires review' }] }
  })
}

export const demoUpvote = (id: string) => update(id, (i) => ({ ...i, voted: !i.voted, votes: i.votes + (i.voted ? -1 : 1) }))
export const demoComment = (id: string, text: string) => update(id, (i) => ({ ...i, comments: [...i.comments, { who: 'You', text, at: new Date().toISOString() }] }))

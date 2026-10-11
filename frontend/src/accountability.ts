// DEMO OVERLAY — the backend has no budget, MLA or contractor data. Every name below is fictional placeholder data
// and every amount is a rough estimate. Replace with official records (e.g. GHMC works registry) when an API exists.
import type { Summary } from './types'

export interface Constituency { name: string; mla: string; budgetCr: number; spentCr: number }
export const CONSTITUENCIES: Constituency[] = [
  { name: 'Serilingampally', mla: 'Smt. Kavitha Rao', budgetCr: 42, spentCr: 27.5 }, { name: 'Jubilee Hills', mla: 'Shri R. Venkat Reddy', budgetCr: 30, spentCr: 21.2 },
  { name: 'Kukatpally', mla: 'Shri M. Srinivas Goud', budgetCr: 38, spentCr: 31.9 }, { name: 'Sanathnagar', mla: 'Smt. Lakshmi Prasad', budgetCr: 22, spentCr: 12.4 },
  { name: 'Secunderabad', mla: 'Shri A. Imran Khan', budgetCr: 26, spentCr: 18.8 }, { name: 'Charminar', mla: 'Shri S. Mohammed Ali', budgetCr: 24, spentCr: 20.6 },
  { name: 'LB Nagar', mla: 'Shri K. Ravinder Yadav', budgetCr: 34, spentCr: 22.1 }, { name: 'Khairatabad', mla: 'Smt. P. Anitha Reddy', budgetCr: 28, spentCr: 15.3 },
  { name: 'Goshamahal', mla: 'Shri N. Rajasekhar', budgetCr: 20, spentCr: 14.7 }, { name: 'Malakpet', mla: 'Shri T. Abdul Rahim', budgetCr: 21, spentCr: 11.9 },
]
const ZONES: [string, string][] = [['Madhapur', 'Serilingampally'], ['Gachibowli', 'Serilingampally'], ['Jubilee', 'Jubilee Hills'], ['Kukatpally', 'Kukatpally'], ['Ameerpet', 'Sanathnagar'],
  ['Secunderabad', 'Secunderabad'], ['Charminar', 'Charminar'], ['Dilsukhnagar', 'LB Nagar'], ['Lakdikapul', 'Khairatabad'], ['Abids', 'Goshamahal'], ['Koti', 'Malakpet']]
const hash = (s: string) => [...s].reduce((a, c) => (a * 31 + c.charCodeAt(0)) >>> 0, 7)
export const constituencyOf = (zone: string) => {
  const hit = ZONES.find(([k]) => zone.includes(k))?.[1]
  return CONSTITUENCIES.find((c) => c.name === hit) ?? CONSTITUENCIES[hash(zone) % CONSTITUENCIES.length]
}

const COST: Record<string, number> = { Pothole: 18000, RoadDamage: 240000, BrokenStreetlight: 6500, Streetlight: 6500, GarbageAccumulation: 4000, Garbage: 4000, IllegalDumping: 9000,
  SewageOverflow: 85000, WaterLeakage: 45000, Waterlogging: 70000, DamagedFootpath: 120000, BrokenFootpath: 120000, Drain: 60000, FallenTree: 15000 }
const CONTRACTORS: Record<string, string> = { road: 'Deccan Roadworks Pvt Ltd (demo)', light: 'Lumina Electricals (demo)', water: 'Musi Drainage & Water Works (demo)', foot: 'Charminar Civil & Co (demo)', clean: 'GHMC Sanitation (in-house)' }
const kind = (c: string) => /pothole|road/i.test(c) ? 'road' : /light/i.test(c) ? 'light' : /sewage|water|drain/i.test(c) ? 'water' : /foot/i.test(c) ? 'foot' : /garbage|dump|tree/i.test(c) ? 'clean' : 'road'

export interface Funding { constituency: Constituency; estCost: number; head: string; contractor: string; warranty?: { until: Date; active: boolean } }
export function fundingOf(i: Summary): Funding {
  const k = kind(i.category), h = hash(i.id + i.code)
  const warrantied = k === 'road' || k === 'foot'
  const built = new Date(2023, h % 30, 1), until = new Date(built.getFullYear() + 3, built.getMonth() + 1, 1) // 3-year defect liability period (illustrative)
  return { constituency: constituencyOf(i.zoneName ?? ''), estCost: Math.round((COST[i.category] ?? 25000) * (0.7 + i.priorityScore / 150) / 500) * 500,
    head: { road: 'Roads & Pavements', light: 'Street Lighting', water: 'Drainage & Water', foot: 'Roads & Pavements', clean: 'Sanitation' }[k], contractor: CONTRACTORS[k],
    warranty: warrantied ? { until, active: until.getTime() > Date.now() } : undefined }
}
export const inr = (n: number) => new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', maximumFractionDigits: 0 }).format(n)
export const cr = (n: number) => `₹${n.toFixed(1)} Cr`

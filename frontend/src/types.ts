// View models. Shapes follow the backend DTOs (IncidentSummaryDto, PriorityBreakdownDto, ...) so live data can map 1:1.
export type Role = 'Citizen' | 'Authority' | 'FieldWorker'
export type WorkStatus = 'Reported' | 'Acknowledged' | 'Assigned' | 'InProgress' | 'WorkCompleted'
export type ResolutionStatus = 'Unverified' | 'AwaitingVerification' | 'Verified' | 'Disputed'
export type Band = 'Low' | 'Medium' | 'High' | 'Critical'

export interface Factor { label: string; points: number; max: number; reason: string }
export interface TimelineItem { at: string; message: string }
export interface Comment { who: string; text: string; at: string }
export interface Incident {
  id: string; code: string; category: string; categoryLabel: string
  location: string; zone: string; lat: number; lon: number
  score: number; amplifier: number; band: Band; factors: Factor[]
  status: WorkStatus; resolution: ResolutionStatus
  observationCount: number; confirmationCount: number
  firstObservedAt: string; assignee?: string
  recurrence?: string; context: string[]
  image: string; votes: number; voted?: boolean; fixers: string[]; comments: Comment[]
  timeline: TimelineItem[]; afterNote?: string; citizenVotes: { fixed: number; stillExists: number }
}
export interface Match { incidentId: string; sameIncidentPercent: number; verdict: string; reason: string; distanceMeters: number }
export interface Analysis {
  category: string; categoryLabel: string; confidencePercent: number; severityLabel: string; reason: string
  provider: string; alternatives: { value: string; label: string }[]; matches: Match[]
}
export interface SubmitResult { incidentId: string; code: string; isNew: boolean; before: number; after: number; message: string; reasons: string[] }
export const CATEGORIES = [
  { value: 'Pothole', label: 'Pothole' }, { value: 'Garbage', label: 'Garbage overflow' },
  { value: 'Streetlight', label: 'Streetlight outage' }, { value: 'Waterlogging', label: 'Waterlogging' },
  { value: 'BrokenFootpath', label: 'Broken footpath' }, { value: 'Drain', label: 'Open / blocked drain' },
]

export const MAX_IMAGE_MB = 15 // mirrors backend ImageTools.MaxBytes
export function checkPhoto(f: File | null): string | null {
  if (!f) return 'Please attach a photo — it is the evidence for this observation.'
  if (/hei[cf]/i.test(f.type) || /\.hei[cf]$/i.test(f.name)) return 'HEIC/HEIF photos are not supported yet. Switch your camera to “Most Compatible” (JPEG) or upload a JPEG/PNG.'
  if (!/^image\//.test(f.type)) return 'That file is not an image.'
  if (f.size > MAX_IMAGE_MB * 1024 * 1024) return `The image is too large. Please use a photo under ${MAX_IMAGE_MB} MB.`
  return null
}

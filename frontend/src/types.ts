export type Role = "Citizen" | "Authority" | "FieldWorker";
export interface User {
  id: string;
  name: string;
  email: string;
  role: Role;
  trustScore: number;
}
export interface Auth {
  token: string;
  expiresAt: string;
  user: User;
}
export interface Page<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}
export interface Summary {
  id: string;
  code: string;
  category: string;
  categoryLabel: string;
  latitude: number;
  longitude: number;
  locationLabel?: string;
  priorityScore: number;
  priorityBand: string;
  status: string;
  resolutionStatus: string;
  observationCount: number;
  confirmationCount: number;
  firstObservedAt: string;
  lastUpdated: string;
  assigneeName?: string;
  assignedWorkerId?: string;
  isRecurring: boolean;
  primaryImageUrl?: string;
  zoneName?: string;
}
export interface Feed {
  summary: Summary;
  votes: number;
  voted: boolean;
  commentCount: number;
  fixers: string[];
}
export interface Comment {
  id: string;
  authorLabel: string;
  text: string;
  createdAt: string;
  canDelete: boolean;
}
export interface Social {
  votes: number;
  voted: boolean;
  commentCount: number;
  comments: Page<Comment>;
}
export interface Resolution {
  evidenceId: string;
  beforeImageUrl?: string;
  afterImageUrl: string;
  workerNotes?: string;
  verificationResult: string;
  aiConfidencePercent: number;
  aiReason?: string;
  neutralNote?: string;
  outcomeLabel: string;
  resolutionConfidencePercent: number;
  citizens: {
    fixed: number;
    stillExists: number;
    unsure: number;
    total: number;
  };
}
export interface Detail {
  summary: Summary;
  priority: {
    severity: number;
    citizenEvidence: number;
    locationRisk: number;
    incidentAge: number;
    exposure: number;
    recurrence: number;
    contextAmplifier: number;
    reasons: string[];
  };
  context: { reasons: string[] };
  recurrence: { isRecurring: boolean; summary: string };
  isContributor: boolean;
  memory: {
    incidentId: string;
    code: string;
    categoryLabel: string;
    at: string;
    status: string;
  }[];
  correlations: {
    targetIncidentId: string;
    targetCode: string;
    explanation: string;
    confidenceLabel: string;
  }[];
  timeline: { at: string; message: string; eventType: string }[];
  resolution?: Resolution;
  allowedNextStatuses: string[];
  canConfirm: boolean;
  canVerifyResolution: boolean;
  myVote?: string;
  statusBanner: string;
  observations: {
    id: string;
    imageUrl: string;
    createdAt: string;
    category: string;
  }[];
}
export interface Match {
  incidentId: string;
  code: string;
  categoryLabel: string;
  sameIncidentPercent: number;
  reason: string;
  distanceMeters: number;
}
export interface Analysis {
  draftId: string;
  imageUrl: string;
  detectedCategory: string;
  detectedCategoryLabel: string;
  confidencePercent: number;
  severityLabel: string;
  reason: string;
  provider: string;
  alternatives: { category: string; label: string }[];
  matches: Match[];
  warnings: string[];
  context: { locationRiskLabel: string; reasons: string[] };
}
export interface SubmitResult {
  incidentId: string;
  code: string;
  priorityBefore: number;
  priorityAfter: number;
  isNewIncident: boolean;
  message: string;
  reasons: string[];
}
export interface DemoInfo {
  demoMode: boolean;
  city?: {
    name: string;
    reportLatitude: number;
    reportLongitude: number;
    reportLocationLabel: string;
  };
  accounts?: { role: string; email: string; password: string }[];
}
export interface Worker {
  id: string;
  name: string;
  activeAssignments: number;
}
export interface Dashboard {
  activeIncidents: number;
  totalObservations: number;
  highPriorityIncidents: number;
  criticalIncidents: number;
  awaitingVerification: number;
  disputedResolutions: number;
  civicHealthScore: number;
}
export interface Scenario {
  id: number;
  name: string;
  description: string;
}
export interface ScenarioResult {
  name: string;
  incidentId?: string;
  summary: string;
  highlights: string[];
}
export interface Notification {
  id: string;
  title: string;
  message: string;
  isRead: boolean;
  createdAt: string;
  incidentId?: string;
}
export const CATEGORIES = [
  ["Pothole", "Pothole"],
  ["GarbageAccumulation", "Garbage accumulation"],
  ["WaterLeakage", "Water leakage"],
  ["SewageOverflow", "Sewage overflow"],
  ["BrokenStreetlight", "Broken streetlight"],
  ["RoadDamage", "Road damage"],
  ["DamagedFootpath", "Damaged footpath"],
  ["FallenTree", "Fallen tree"],
  ["IllegalDumping", "Illegal dumping"],
  ["DamagedPublicInfrastructure", "Damaged public infrastructure"],
  ["Other", "Other"],
].map(([value, label]) => ({ value, label }));
export const MAX_IMAGE_MB = 15;
export function checkPhoto(f: File | null): string | null {
  if (!f) return "Please attach a photo.";
  if (!["image/jpeg", "image/png", "image/webp"].includes(f.type))
    return "Upload JPEG, PNG or WebP. HEIC/HEIF is not supported.";
  if (f.size <= 0 || f.size > MAX_IMAGE_MB * 1024 * 1024)
    return `Upload a nonempty photo under ${MAX_IMAGE_MB} MB.`;
  return null;
}

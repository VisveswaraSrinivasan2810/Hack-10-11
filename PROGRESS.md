# CivicLens AI — Implementation Progress

Status date: 2026-10-11. Classification: **IMPLEMENTED** (built and verified), **PARTIALLY_IMPLEMENTED**, **DEFERRED** (with reason).

## Verification summary

| Check | Result |
|---|---|
| `dotnet build` (Domain, Application, Infrastructure, Api, Tests) | OK, 0 errors |
| `dotnet test` | **117 / 117 passing** (unit + integration over the real HTTP pipeline) |
| Real API process (`dotnet run`, in-memory DB) | Started; jury story verified by curl (CL-1027, 96% pothole, school 72 m, match 96% at 11 m, obs 1→2, CPS 82.2→87.5 CRITICAL; ghost closure → RESOLUTION DISPUTED) |
| EF migration `InitialCreate` | Generated; SQL script inspected (PostGIS extension, `geography` columns, GiST indexes); `has-pending-model-changes` = none |
| **Run against real PostgreSQL/PostGIS** | **NOT VERIFIED** — no Docker/Postgres on the build machine. Tests use the EF InMemory provider. |
| **Angular frontend build/run** | **NOT STARTED** — Node/npm not installed on the build machine (see DEFERRED) |
| Claude (real AI) provider against the live API | **NOT VERIFIED** — compiled and fallback-tested only (no API key) |

## Backend requirements

| Area | Status | Notes |
|---|---|---|
| Solution layout (Domain / Application / Infrastructure / Api / Tests), modular monolith, DI, async | IMPLEMENTED | |
| PostgreSQL + PostGIS schema, `geography(Point)`/`geography(Polygon)`, GiST indexes, migration | IMPLEMENTED (schema) / PARTIALLY (runtime unverified) | See verification table |
| Proximity queries "use PostGIS" | PARTIALLY_IMPLEMENTED | Lat/Lon bounding box (indexed) + exact haversine in-process. Location columns are persisted and GiST-indexed but `ST_DWithin` is not used yet. Chosen so one verified code path works on every provider. |
| Hotspots "use PostGIS to cluster" | PARTIALLY_IMPLEMENTED | Same density clustering as `ST_ClusterDBSCAN` (single linkage, eps 150 m, minPts 3) implemented in C# and unit-tested; not run in SQL. |
| JWT auth, roles (Citizen/Authority/FieldWorker), BCrypt, role-restricted endpoints | IMPLEMENTED | Self-registration can only create citizens. |
| FluentValidation, consistent envelope `{success,data,error,correlationId}`, readable errors, correlation id, structured logging (Serilog), Swagger | IMPLEMENTED | 37 documented paths. |
| Rate limiting | IMPLEMENTED | Global/auth/upload HTTP limiters + configurable per-user observations/hour (domain-level, returns 429). |
| Upload validation (magic bytes, 8 MB), EXIF/XMP/PNG-text stripping, no filesystem paths exposed | IMPLEMENTED | JPEG + PNG stripping; WebP kept as-is. |
| CivicIncident-centric model (observations → incidents) | IMPLEMENTED | |
| Same-incident fingerprint (0.40/0.30/0.20/0.10), 0.85 / 0.65 thresholds, 50 m radius, human override, audit of overrides | IMPLEMENTED | Quadratic spatial falloff. |
| CPS (6 weighted factors, bands, reasons) | IMPLEMENTED | **Deliberate deviation:** added a configurable *context amplifier* (`ContextAmplifierMax`, default 0.75; set 0 for the plain formula). With age = 0 and recurrence = 0 the plain formula cannot exceed 75, so the specified demo (76 → 88, CRITICAL, brand-new school pothole) was mathematically unreachable. The amplifier is shown in the score breakdown. |
| Contextual risk from POIs | IMPLEMENTED | Per-type radius; explanation text ("72 m from a school"). |
| Proof of resolution (separate OperationalStatus / ResolutionStatus), AI before/after check, citizen votes, combined confidence | IMPLEMENTED | |
| Ghost closure detection (neutral wording, "Disputed Resolutions" queue) | IMPLEMENTED | |
| Recurrence intelligence + CPS influence | IMPLEMENTED | Counts resolved same-family incidents within 50 m / 365 d. |
| Infrastructure Memory | IMPLEMENTED | API + in incident detail. |
| Cross-issue correlation (water leakage → road damage) | IMPLEMENTED | Non-causal language. |
| Hotspots, Civic Health Score (zones), Citizen Trust Score | IMPLEMENTED | Transparent capped formulas, unit-tested. |
| Fraud / abuse: duplicate image hash, location validation, rate limit, AI confidence threshold, human override, audit trail | IMPLEMENTED | Same image by another citizen is allowed but flagged. |
| State machine (13 statuses, actor-restricted transitions, history) | IMPLEMENTED | |
| Notifications (in-app), audit timeline | IMPLEMENTED | |
| AI abstraction: `IIssueClassificationService`, `IImageSimilarityService`, `IResolutionVerificationService`, `IContextRiskService`, `IPriorityScoringService`, `IIncidentMatchingService` | IMPLEMENTED | |
| DemoAIProvider (deterministic, offline) | IMPLEMENTED | Driven by file-name/description hints; image "similarity" is a byte-histogram heuristic (not semantic). |
| RealAIProvider (Claude vision via official SDK) | PARTIALLY_IMPLEMENTED | `AI:Provider=Claude`; wrapped by fallback to demo on any failure. Fallback verified by test; live API calls unverified. |
| DEMO_MODE seeding: users, 26 incidents (15 active, 10 resolved, 1 rejected), 3 critical, 3 recurring locations, 2 disputed, 6 POIs, 3 zones, hotspot, correlation | IMPLEMENTED | Asserted by `SeedStateTests`. Next live incident is CL-1027. |
| Demo scenarios 1–10 (run through the real services) | IMPLEMENTED | `POST /api/demo/scenarios/{id}/run` (authority only). Scenario 9 backdates the leak by 10 days (documented in the audit trail). |
| Unit tests (CPS, matching, distance, recurrence, transitions, verification calc, health, trust, correlation, hotspots, images, providers) | IMPLEMENTED | |
| Integration tests (observation→incident, →existing, assignment, resolution, verification, failure→review) + authorization + validation + AI outage | IMPLEMENTED | |
| Docker Compose (PostGIS + API), Dockerfile, `.env.example` | IMPLEMENTED (files) | `docker compose up` itself not run here. |
| Zero-dependency run mode (`Database__Provider=InMemory`) | IMPLEMENTED | Extra; data lost on restart. |

## Frontend requirements

| Area | Status | Reason |
|---|---|---|
| Angular app (citizen, authority, worker routes), Angular Material, Leaflet/OSM, Chart.js, PWA | **DEFERRED** | Node.js/npm are not installed on the build machine, so Angular could not be built or verified; the user chose to proceed with .NET first. Per the definition of DONE, no UI is claimed. All screens/data they need are served by the API (see README → API). |
| Loading / empty / error states, marker clustering, map fallback list view | **DEFERRED** | Part of the frontend. |
| Screenshots in README | **DEFERRED** | No UI exists yet. |

## Edge cases (spec §38)

Handled & tested at API level: unsupported image, image too large, missing image, invalid/out-of-area location, device-vs-pin mismatch, low AI confidence, AI unavailable (classification and resolution), no duplicate, multiple candidates, incorrect duplicate suggestion (override audited), citizen changes category, resolved incident receives new observation (reopen), reopened issue, worker unavailable (reassign), invalid state transition, conflicting citizen votes, no citizens verifying (authority manual verification with reason), duplicate image, worker reuses before-photo, same category nearby but separate, different category same spot, authority override, spam (rate limit), recurrence after long time (365-day window).
Client-side only (**DEFERRED** with the frontend): location/camera permission denied, no GPS, upload failure UX, offline/network failure.

## Known limitations

* PostgreSQL runtime path unverified (see above); first thing to do when Docker is available: `docker compose up`, then `dotnet test` against it.
* `ImageSimilarity` demo provider is a heuristic; real embeddings are a drop-in `IImageSimilarityService`.
* Incident codes use count + 1001 with a uniqueness check (safe for a single instance; a DB sequence would be better under concurrency).

> The React MVP is now connected to the API. Start the API with `dotnet run --project src/CivicLens.Api --launch-profile Demo`, then run `npm ci` and `npm run dev` in `frontend`. Open the frontend on port 4200. See [frontend instructions](frontend/README.md) and [API/production requirements](docs/API_INTEGRATION.md). No tests were written or run for this integration.

# CivicLens AI
### From Citizen Observation to Verified Resolution

> **Hyderabad seed:** Current demo geography, landmarks, scenarios and report defaults use fictional Hyderabad fixtures. Restart the in-memory demo after pulling; see [DEMO.md](DEMO.md).

> **MVP browser UI added:** See [DEMO.md](DEMO.md) for the one-command local demo, role switching and jury walkthrough. The browser UI uses plain JavaScript served by the API. No Node.js install is needed.

> **Earlier backend status:** the earlier backend work includes domain intelligence, API, seed data and demo scenarios. A plain JavaScript MVP browser interface is now available; a separate Angular implementation is still deferred. See [DEMO.md](DEMO.md) for current demo instructions. No tests were run or added for the MVP UI changes.

## Problem

Citizens meet potholes, garbage, leaks and broken lights every day, but they cannot tell whether someone already reported the problem, who owns it, how serious it is, whether it was acted on, whether a "closed" complaint was truly fixed, or whether it keeps coming back. Authorities receive a stream of fragmented complaints instead of a clean picture of real-world problems.

## Why existing complaint systems are insufficient

They model **Complaint → Ticket → Closed**. Four reports of one pothole become four tickets; priority is manual; "closed" is self-declared, so ghost closures go unnoticed; and the city never learns that the same junction fails every quarter.

## Solution

CivicLens models the **physical problem itself**: `Observation → Intelligence → Incident → Priority → Action → Proof → Learning`.

## Key Innovation

> Existing civic systems primarily manage complaints. CivicLens models the physical problem itself.

* **One real-world problem ≠ many complaints** – observations merge into one `CivicIncident` through a transparent fingerprint score, never silently.
* **Explainable priority** (CPS) with contextual risk (a school 72 m away changes urgency).
* **Don't just close tickets – verify outcomes** – work-completed is not resolved; AI before/after checks + citizen votes decide, and failures surface as *Resolution Disputed – Requires Review* (neutral wording, never an accusation).
* **Infrastructure memory, recurrence, cross-issue correlation, hotspots, zone health, citizen trust.**

## Architecture

```
src/
  CivicLens.Domain          pure logic: entities, state machine, CPS, fingerprint, recurrence, trust, health, hotspots (no I/O)
  CivicLens.Application     services, DTOs, validators, AI/storage abstractions, demo seeder + scenarios
  CivicLens.Infrastructure  EF Core + PostGIS, migrations, demo & Claude AI providers, JWT, BCrypt, local image storage
  CivicLens.Api             controllers, envelope, correlation id, rate limits, Swagger
tests/CivicLens.Tests       117 unit + integration tests (real HTTP pipeline)
```
Modular monolith; no microservices, broker or Kubernetes. Domain logic never lives in controllers.

## Tech Stack

.NET 8 · ASP.NET Core · EF Core 8 · PostgreSQL + PostGIS (NetTopologySuite) · JWT · FluentValidation · Serilog · Swagger · xUnit · Docker Compose · optional Anthropic SDK (Claude vision). *Planned frontend:* Angular, Angular Material, Leaflet/OSM, Chart.js.

External dependencies: PostgreSQL/PostGIS (Docker image `postgis/postgis`), and — only if `AI__Provider=Claude` — the Anthropic API. Everything else runs offline.

## Setup

Prerequisite: .NET 8 SDK.

**A. Zero dependencies (fastest; in-memory data, lost on restart)**
```bash
cd src/CivicLens.Api
Database__Provider=InMemory ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile --urls http://localhost:5241
# open http://localhost:5241/swagger
```
**B. With PostgreSQL + PostGIS (the real target)**
```bash
cp .env.example .env        # set JWT_KEY (32+ chars)
docker compose up --build   # db + api on http://localhost:5241
# or only the db:  docker compose up db   and   dotnet run --project src/CivicLens.Api
```
Migrations are applied automatically at startup. `DEMO_MODE=true` (default in Development) seeds the demo city.

> The Postgres path has not been executed on the build machine (no Docker there); see PROGRESS.md.

## Demo Accounts (development only — documented password)

| Role | Email | Password |
|---|---|---|
| Citizen | `citizen@civiclens.demo` (also `citizen2@civiclens.demo`) | `Demo@12345` |
| Authority | `authority@civiclens.demo` | `Demo@12345` |
| Field worker | `worker@civiclens.demo` (also `worker2@civiclens.demo`) | `Demo@12345` |

`GET /api/demo/info` lists them when `DEMO_MODE=true`. Never enable demo mode in production.

## Demo Scenario

Seeded city: 26 incidents (15 active, 10 resolved, 1 rejected), 3 critical, 3 recurring locations, 2 disputed resolutions, an emerging hotspot at *Demo Junction*, a water-leak → road-damage correlation, 6 POIs, 3 zones.

**Photo cues (DemoAIProvider):** the demo vision is deterministic and keyed on the **file name / description**: `pothole*.jpg`, `garbage*`, `water_leak*`, `sewage*`, `streetlight*`, `road_damage*`, `footpath*`, `tree*`, `dumping*`. A name with no cue (e.g. `IMG_0001.jpg`) is *low confidence* (51/32/17 %) so the "please choose" flow is demonstrable. For after-photos, `fixed`/`repaired`/`cleaned` → issue not detected (≈95 %); a cue for the same issue or `still_present` → issue still present (≈22 %) → ghost closure.

**Jury story (verified by curl against the running API):**
1. Citizen uploads `pothole_school_road.jpg` at 12.97185, 77.5940 → *Pothole 96 %*, "72 m from a school", location risk HIGH → incident **CL-1027**, CPS 82.
2. Citizen 2 uploads at +11 m → *Possible Existing Incident CL-1027, 11 m, 96 %* → confirms → observations 1 → 2, **CPS 82.2 → 87.5 CRITICAL**.
3. Authority assigns the worker; worker accepts, starts, uploads `pothole_fixed_after.jpg` → AI 9x %; a citizen confirms → **VERIFIED RESOLVED**.
4. Ghost closure: upload `pothole_still_present_after.jpg` instead → **RESOLUTION DISPUTED — REQUIRES REVIEW**, listed in `/api/dashboard/disputed`.

**Scenario runner** (authority token): `GET /api/demo/scenarios`, `POST /api/demo/scenarios/{1..10}/run`, `POST /api/demo/reset`. They drive the real services: 1 new unique pothole · 2 duplicate observation · 3 critical pothole near school · 4 garbage · 5 water leakage · 6 recurring road damage · 7 successful resolution · 8 failed verification · 9 cross-issue correlation · 10 civic hotspot.

## Civic Incident Fingerprint

`SameIncidentScore = 0.40·spatial + 0.30·category + 0.20·visual + 0.10·temporal`, each 0–1. Spatial = `1 − (d/50 m)²`; category 1 / 0.7 (pothole↔road damage, garbage↔dumping) / 0; visual from `IImageSimilarityService`; temporal decays over 90 days. **≥ 0.85** very likely same · **0.65–0.84** ask the citizen · **< 0.65** new. Uncertain matches are never merged silently; the citizen can always create a separate incident (audited).

## Civic Priority Score

`CPS = 0.25·Severity + 0.15·CitizenEvidence + 0.20·LocationRisk + 0.10·Age + 0.15·Exposure + 0.15·Recurrence` (weights configurable), bands 0–29 LOW · 30–49 MODERATE · 50–69 HIGH · 70–84 VERY HIGH · 85–100 CRITICAL. Every incident returns the factor values and "Why Critical?" reasons. A configurable **context amplifier** (≤ ×1.75 when location risk > 50; `ContextAmplifierMax=0` disables it) is added because the plain formula cannot exceed 75 for a brand-new incident — see PROGRESS.md.

## Resolution Verification

`OperationalStatus` (state machine) and `ResolutionStatus` are separate. The worker uploads an after-photo → system moves WorkCompleted → AwaitingVerification → AI compares → combined confidence = 0.6·AI + 0.4·citizen (trust-weighted votes). **Verified** needs confidence ≥ 0.75 *and* a citizen "Fixed" (or an audited authority override). **Disputed** if AI < 0.5, citizens mostly say "still exists", or the combined score is < 0.5. Physical evidence always wins: one "Fixed" vote cannot overturn a failed visual check.

## Screenshots

Open the local demo home page for the browser UI. Swagger remains available at `/swagger`. Screenshots have not been captured; interactive browser behavior remains untested.

## API Documentation

Swagger UI at `/swagger` (JWT bearer). Envelope: `{ "success", "data", "error", "correlationId" }`.
Key endpoints: `POST /api/auth/login|register` · `POST /api/observations/analyze` (multipart) · `POST /api/observations` · `GET /api/incidents[/nearby|/{id}|/{id}/matches]` · `POST /api/incidents/{id}/confirm|assign|status|override|resolution|verify` · `GET /api/dashboard/summary|hotspots|recurring|disputed|verification-queue|analytics|correlations` · `GET /api/zones[/{id}/health]` · `GET /api/pois` · `GET /api/memory` · `GET /api/users/me[/contributions|/trust|/notifications]` · `/api/demo/*`.

## Testing

```bash
dotnet test        # 117 tests: unit (scoring, matching, geo, recurrence, state machine, verification, health, trust, hotspots, images, providers)
                   # + integration (observation→incident, →existing, assignment, resolution, verification, failure→review, authorization, AI outage, rate limit, all 10 demo scenarios)
```
Integration tests boot the real API on an EF InMemory database.

## Uploading any photo (MVP flow)

The MVP is upload-based (`POST /api/observations/analyze`, multipart). Any JPEG/PNG/WebP up to 15 MB works: the server fixes phone rotation, strips all metadata (including GPS), downsizes it, and compares photos with a real perceptual thumbnail similarity.

* **No API key (default for a demo):** the demo classifier recognises photos by **file name** (`pothole.jpg`, `garbage.jpg`, …). A photo with an unrecognised name (e.g. `IMG_1234.jpg`) returns *low confidence* and the user simply picks the category: that flow is built in.
* **With a Claude key:** set `ANTHROPIC_API_KEY` (or `AI__ApiKey`) and any real photo is classified by Claude vision automatically (`AI__Provider=Auto` is the default; `AI__Model` defaults to `claude-opus-5-5` and can be set to a cheaper vision model). In `DEMO_MODE` a failed call falls back to the demo classifier; otherwise the user chooses the category.
* **Outside the demo city:** the first report in an area with no known places imports schools/hospitals/bus stops from OpenStreetMap (disabled in `DEMO_MODE`, which stays fully offline). Authorities can also manage places, zones and staff accounts under `/api/admin/*`.

Not part of this MVP: deployment hardening, a real PostgreSQL run, the Angular UI (see PROGRESS.md).

## Future Scope (not implemented)

Municipal API integration · IoT sensors · CCTV detection where legal · weather correlation · predictive maintenance · utility integration · contractor analytics · SLA management · open civic datasets · multilingual voice reporting · accessibility · advanced computer vision · infrastructure digital twins. *(The Angular UI and PostGIS-native queries are the next engineering steps — see PROGRESS.md.)*

## Hackathon Pitch

> Existing civic systems primarily manage complaints. CivicLens models the physical problem itself.

> Multiple citizen reports can represent one real-world incident. CivicLens combines spatial context, visual evidence and citizen corroboration to build a continuously evolving representation of that incident.

**ONE REAL-WORLD PROBLEM ≠ MANY COMPLAINTS. DON'T JUST CLOSE TICKETS. VERIFY OUTCOMES.**

> Existing systems measure complaints and closures. CivicLens measures real-world incidents and verified resolutions.

## Free hosted MVP

Use the Render Blueprint in [`render.yaml`](render.yaml) to run the UI and .NET
API together on a disposable free-tier service. See [the deployment steps](DEMO.md#free-hosted-demo-on-render).
No PostgreSQL or AI key is needed; accounts, incidents and uploads are demo-only.

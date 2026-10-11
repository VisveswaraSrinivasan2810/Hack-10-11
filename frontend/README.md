# CivicLens web (prototype)

React + Vite + TypeScript. Runs fully offline on deterministic demo data.

    npm install && npm run dev      # http://localhost:4200
    cp .env.example .env            # optionally set VITE_API_BASE_URL (e.g. http://localhost:5000)

## Backend integration status
Inspected `src/CivicLens.Api/Controllers`. Every route except `GET /api/demo/info` requires a JWT (`[Authorize]`), and this prototype deliberately has no auth. So:
- Live: `GET /api/demo/info` (reachability probe in `src/api.ts`).
- Demo data: observe/analyze, submit, incidents, assign, status, resolution, verify, dashboard.
- Backend CORS allows `http://localhost:4200` by default (matches the dev port).
Role switcher is a local preview, not authentication.

## Backend sync (latest pull)
New since last inspection: `/api/admin/*` (POI/zone/staff management, Authority-only), `POST /api/account/password`, a `ContextAmplifier` on the priority breakdown, HEIC rejection and a 15 MB image cap, and 409 conflict envelopes. All still require JWT, so only `GET /api/demo/info` is live from this UI. Frontend adaptations: client-side photo checks (JPEG/PNG, not HEIC, ≤ 15 MB), and a "context amplifier" line in the score explanation. Admin and account screens are out of scope (they need auth).

## Hosted demo

Render builds this UI with the committed npm lockfile, then serves it at `/` from
the .NET container. The feed's votes, comments and workflow previews remain local
simulations. Use **Open backend-connected workspace** (`/operations.html`) for
actual API reporting, assignment, resolution and the ten backend demo scenarios.
The two demo data stores are independent; frontend interactions do not mutate
backend incidents. No tests were run or added for hosting integration.

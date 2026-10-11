# CivicLens — hackathon MVP

A browser-based civic incident demo: citizen reporting → contextual priority →
authority assignment → field-worker evidence → citizen verification.

## Start on Windows (your `C:\Hack` checkout)

Install the **.NET 8 SDK**, open PowerShell in the repository folder, then run:

```powershell
dotnet run --project src/CivicLens.Api --launch-profile Demo
```

Open **http://localhost:5241** on your own computer. The same command works on
macOS/Linux with .NET 8 installed. Port 5241 is for this local demo only.

**No Docker, PostgreSQL, API key, Node.js or frontend install needed.** Development
uses the existing in-memory database and deterministic demo AI. The browser UI is
served directly by the API. A random development JWT key is generated on startup.
Data and sessions reset when the server restarts. This is an MVP demo, not a
production configuration. Demo mode is rejected outside Development.

If the port is occupied, stop the other app or run:

```powershell
dotnet run --project src/CivicLens.Api --launch-profile Demo --urls http://localhost:5242
```

Then open port 5242 instead. Swagger remains available at `/swagger`.

## Your 3-minute presentation

1. The app opens as **Authority** with a seeded Hyderabad demo city.
2. Open **Demo studio** and run scenario **2 — Duplicate pothole observation**.
   Open the resulting incident: two observations are merged into one incident.
3. Run **3 — Critical pothole near school**. Show the contextual priority reasons.
4. Run **7 — Successful resolution**. Open the timeline and evidence: completion
   is verified, not simply declared.
5. Run **8 — Failed resolution verification**. Show the disputed resolution:
   CivicLens catches a ghost closure.
6. Return to **City overview** to show computed city and neighborhood health.

The scenario buttons call the existing backend services, using synthetic photo
inputs and simulated AI. They are demo features, not live AI integrations or real
city reports. **Reset demo city** restores the starting data; it is destructive to
this disposable demo database only.

## Hands-on role flow

Use the account selector at the top right; demo login is automatic.

- **Citizen:** report an issue with a JPEG/PNG/WebP photo and coordinates; review
  the AI suggestion; create a report or join an existing match; confirm incidents
  and vote on resolution when the backend permits it.
- **Authority:** inspect priorities and timelines, assign a field worker, update
  allowed statuses and run the guided scenarios.
- **Field worker:** see assigned incidents, accept work, start work and upload an
  after-photo for verification.

Demo AI uses description/file-name hints, not real visual understanding. Describe
potholes/garbage/water leakage explicitly for a predictable manual demonstration.
All scenario outcomes are simulated. The map is an illustrative coordinate plot,
not a navigation map. Synthetic scenario photos are placeholders; seeded SVGs
provide the illustrative evidence images.

Demo accounts: `authority@civiclens.demo`, `citizen@civiclens.demo`, and
`worker@civiclens.demo`. Development-only password: `Demo@12345`.

## What changed

- Added a responsive browser UI with city overview, incident workspace, demo
  studio, reporting, assignment, evidence submission and verification actions.
- Added the **Demo** launch profile and development-only offline defaults.
- Fixed middleware ordering so authenticated user rate limits use the user identity.
- Added ignore rules for future local build outputs and uploads.
- Removed the proposed production/Docker work to keep this MVP simple.

No tests or test cases are run or added, per request. API compilation is reported
separately; it does not establish that every interactive workflow works. Browser
behavior and end-to-end scenarios remain untested.

## Hyderabad seed

The demo now uses Hyderabad coordinates around **17.3850, 78.4867**, with
fictional Lakdikapul, Abids and Koti demo zones, schools, hospital, market, bus stop
and junction. These are illustrative seed fixtures, not surveyed real facilities
or official municipal boundaries. The default report is at **17.38525, 78.4861**,
about 72 m north of the seeded Abids Demo Public School. All recurring histories,
scenario locations, hotspot bounds and map markers use the same city coordinates.

After pulling this update, stop and restart the **Demo** process. Its in-memory
database is recreated automatically with Hyderabad data. If you explicitly use a
persistent database, existing records are not replaced automatically; use
**Authority → Demo studio → Reset demo city** only on a disposable demo database.
Reset deletes the demo data and reseeds it. Demo AI remains simulated.

## Free hosted demo on Render

The existing .NET API needs a long-running server. Netlify/Vercel static hosting
alone cannot run this backend. `render.yaml` defines a free Docker web service
that serves both the UI and API from the same origin.

1. Sign in to Render and choose **New → Blueprint**.
2. Connect `VisveswaraSrinivasan2810/Hack-10-11`, branch `main`.
3. Render reads `render.yaml`. Confirm that the service plan is **Free** before
   launching. No database or paid disk is defined.
4. Apply the blueprint and wait for its image build and startup to finish.
5. Open the service's HTTPS URL from the Render dashboard. The home page is the
   demo UI; `/swagger` is available for API exploration.

A generated JWT key is configured by Render; no AI credential is needed. Seeded
demo logins are still public demo accounts, but the API enforces role-based login
instead of the optional no-login bypass. This service is deliberately configured
as Development for its disposable demo fixtures; it is not a production release.
Do not enter personal information or sensitive photographs.

The free service can sleep when idle, so open it before presenting to allow time
for a cold start. Incidents and uploads are ephemeral: restart, redeployment or
instance replacement can discard them and recreate the Hyderabad seed. There is
no persistent database or storage. Availability and free-tier account eligibility
are controlled by Render. Deployment is not complete until Render reports the
service live; adding this file to GitHub does not launch a server by itself.

The Docker build publishes only the API and its dependencies; no test runner or
test project is included. No tests or hosted smoke checks were run in this change.

## New feed UI from `frontend-feed-ui`

The hosted home page is now the React/Vite Hyderabad social feed (local simulated
records, votes, comments and role previews). The actual API-connected workspace
is retained at **`/operations.html`**, linked from the feed footer. Use that
workspace for the backend scenario presentation and real request/response flows.
These two demo stores are independent; the feed does not claim live API mutations.

The Render image builds the React frontend using `npm ci` and `npm run build`,
then embeds its output alongside the API. Local `dotnet run` still serves the
original API-connected home page. Run `npm ci` and `npm run dev` inside `frontend`
to preview the new feed separately. The Docker build uses Node 24.

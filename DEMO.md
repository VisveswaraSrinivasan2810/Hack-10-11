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

1. The app opens as **Authority** with a seeded Bengaluru demo city.
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

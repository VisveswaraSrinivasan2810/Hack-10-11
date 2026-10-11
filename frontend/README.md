# CivicLens web

React + Vite + TypeScript UI connected to the .NET API. There is no local mock incident store. Login, citizen reporting, social activity, operator workflow, resolution checks, city intelligence, notifications and demo scenarios all use API requests.

## Run the MVP locally

From the repository root, start the API:

```sh
dotnet run --project src/CivicLens.Api --launch-profile Demo
```

In a second terminal:

```sh
cd frontend
npm ci
npm run dev
```

Open the frontend on port 4200. Vite proxies API, evidence and demo illustrations to the API on port 5241. Leave `VITE_API_BASE_URL` empty. Sign in using a demo account button. Role buttons perform real logins; sessions are held in memory and require login after a page reload.

For a separate frontend host, set `VITE_API_BASE_URL` to the HTTPS API origin at build time and add the frontend origin to API `Cors__Origins__0`. Uploaded evidence is fetched with the bearer token and displayed through temporary blob URLs.

## Present in five minutes

1. Citizen: open an incident, upvote and comment, then upload a real JPEG/PNG/WebP to report a new issue. Confirm the category and choose a matching incident or create a separate one.
2. Authority: open the report, inspect priority reasons and assign an active worker.
3. Field worker: accept, start work and upload the after-photo.
4. Citizen: confirm the outcome; work completion and verified resolution are shown separately.
5. Authority: Demo studio → run scenarios **2 → 3 → 7 → 8**, opening each resulting incident. Show duplicate evidence, school context, verified closure and disputed closure.

Demo AI, illustrations and scenario inputs are explicitly simulated. All records and actions use the backend. InMemory demo data and local uploads are disposable. Reset logs everyone out because account IDs change.

See [API integration and production requirements](../docs/API_INTEGRATION.md). Compilation is checked with `npm run build`; no tests were written or run, as requested.

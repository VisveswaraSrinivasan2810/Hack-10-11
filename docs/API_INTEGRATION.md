# UI/API integration

The React UI is now the main API client. Empty `VITE_API_BASE_URL` means same-origin API calls, not offline mock data. The existing Render image embeds the frontend build. Local development uses the two commands in `frontend/README.md`.

| UI interaction | Endpoint |
| --- | --- |
| Login / citizen signup | POST `/api/auth/login`, `/api/auth/register` |
| Feed with persisted votes, comments, assigned fixer | GET `/api/feed` |
| Upvote / undo | PUT `/api/incidents/{id}/upvote` with `active` |
| Discussion / comment / moderation | GET `/api/incidents/{id}/social`, POST `/comments`, DELETE `/comments/{commentId}` |
| Analyze uploaded evidence | POST `/api/observations/analyze` multipart |
| Recheck matches after category change | GET `/api/observations/drafts/{id}/matches` |
| Create incident / join report | POST `/api/observations` |
| Details, score reasons, history, memory, correlations | GET `/api/incidents/{id}` |
| Confirm additional citizen evidence | POST `/api/incidents/{id}/confirm` |
| Assign / status / audited overrides | POST `/api/incidents/{id}/assign`, `/status`, `/override` |
| Worker completion evidence / citizen outcome | POST `/api/incidents/{id}/resolution`, `/verify` |
| Active workers | GET `/api/users/workers` |
| Authority intelligence, hotspots, analytics, queues | GET `/api/dashboard/summary`, `/hotspots`, `/analytics`, `/verification-queue`, `/disputed` |
| Notifications / mark read | GET `/api/users/me/notifications`, POST `/api/users/me/notifications/read` |
| Password change | POST `/api/account/password` |
| Demo accounts / scenarios / reset | GET `/api/demo/info`, `/scenarios`, POST `/scenarios/{id}/run`, `/reset` |
| Evidence bytes, authenticated and scoped | GET `/uploads/{filename}` |

Filters and vote sorting apply to the current feed page, explicitly labelled. Counts come from the database. Upvotes are social signals; they do not masquerade as independent confirmed observations in the priority score. Fixer count reflects the real current assignee. Comments show role labels rather than citizen identities; owners and authorities can delete them. There is no background live refresh: use Refresh after another device acts.

The API enforces roles and worker assignment even if a client crafts its own request. Tokens remain in browser memory. Password changes and account activation changes rotate a security stamp; expired, deactivated and revoked sessions fail authentication. Existing tokens from before this migration require login again. Auth bypass is disabled by default and permitted only in a Development demo. Production Swagger is disabled. Evidence responses are private, uncached and accessible to their uploader or permitted incident viewers.

PostgreSQL workflow mutations run in serializable transactions. Concurrent conflicts return 409; refresh before manually retrying. No automatic write retries are used. Filesystem writes are outside database transactions: a failed database write may leave an orphan file; production needs retention/cleanup operations. InMemory does not support transactional guarantees.

## Production prerequisites

The configured free Render service is a **disposable Development demo**, not persistent production hosting. Before using real data:

- Set `ASPNETCORE_ENVIRONMENT=Production`, `DEMO_MODE=false`, `Auth__Disabled=false` and `Database__Provider=Postgres` with an explicit PostgreSQL/PostGIS `ConnectionStrings__Default`.
- Supply a private random `Jwt__Key` of at least 32 UTF-8 bytes. The documented development key is rejected in Production. Configure HTTPS at the ingress, correct `AllowedHosts` and only the frontend origins in CORS.
- Use a durable, backed-up `Storage__Path` shared appropriately with the running API. The current disk storage adapter is suitable for one instance, not a distributed deployment. Plan database backups, upload retention and restore operations.
- Apply the migrations, including `PersistFeedAndRevokeSessions`, before production startup. `Database__AutoMigrate=true` is an explicit single-instance alternative requiring schema privileges; the default requires operator-applied migrations. Development automatically migrates relational databases.
- Provision the initial authority from the host process once: supply `Bootstrap__Email`, `Bootstrap__Name`, `Bootstrap__Password` through secure environment configuration, then run the API with `--provision-authority`. This exits after provisioning and refuses if an authority already exists. Remove these variables, start normally and use the authenticated staff API to add workers. No public staff registration exists.
- Configure an actual AI provider if needed. Without a provider, analysis requires manual category selection and human resolution checks; simulated AI is restricted to demo mode. Real photographs must decode, are limited to 15 MB and 24 megapixels, and have metadata removed.
- Use `/health/ready` for database connectivity and `/health` for process liveness. Configure logs, incident monitoring and concurrency/rate-limit capacity for the intended traffic.

The new migration can be applied using the existing EF tool manifest and Infrastructure as startup project:

```sh
dotnet tool restore
dotnet ef database update --project src/CivicLens.Infrastructure --startup-project src/CivicLens.Infrastructure
```

Runtime, browser, database and security acceptance checks were **not executed** because the user requested no tests. Frontend production compilation and API Release compilation are the validation performed. These changes improve production safeguards, but compilation alone does not certify production readiness. Deployment still requires hosting account access; a repository push only triggers hosting if a service is already connected.

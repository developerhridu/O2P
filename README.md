# O2P: Oracle to PostgreSQL Data Migration Platform

O2P is a self-hosted migration control plane for moving Oracle table schemas and bulk data into PostgreSQL. It is designed for large migrations with multiple applications, test/live environments, resumable chunk execution, source throttling, and post-load validation.

## What Is Included

- .NET 8 API with JWT auth, roles, EF Core metadata storage, Data Protection encrypted connection secrets, and preflight endpoints.
- .NET 8 Worker that owns migration execution, queued jobs, table preparation, atomic chunk claiming, expired lease recovery, PostgreSQL COPY loading, and validation.
- React/Vite UI in `web` for login, dashboard, connections, applications, manifests, jobs, job details, discovery, settings, password rotation, and admin user management.
- PostgreSQL metadata database with EF migrations.
- Docker Compose for a single-node deployment and Kubernetes manifests for production hardening.

## Run Locally With Docker Compose

Set strong local secrets first:

```powershell
$env:O2P_METADATA_PASSWORD="replace-with-a-strong-password"
$env:O2P_JWT_SECRET="replace-with-at-least-32-random-characters"
docker compose up --build
```

Open:

- UI: `http://localhost:3000`
- API/Swagger in development: `http://localhost:5000/swagger`
- Metadata PostgreSQL: `localhost:7936`

Default seeded user:

- Username: `admin`
- Password: `AdminPassword123!`

Change the default password before using the system outside local development.

## Public Windows Runtime

The hardened Windows deployment in this repo publishes and serves the stack on:

- UI: `http://<server-ip>:3051`
- API proxy for browser clients: `http://<server-ip>:3052/api/v1`
- Internal API listener: `http://127.0.0.1:5050`

Use these scripts to run the public stack outside Docker:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/run-api-public.ps1 -Port 5050
powershell -ExecutionPolicy Bypass -File scripts/run-proxy-public.ps1 -Port 3052 -TargetPort 5050
powershell -ExecutionPolicy Bypass -File scripts/run-ui-public.ps1 -Port 3051 -ApiBaseUrl http://<server-ip>:3052/api/v1
powershell -ExecutionPolicy Bypass -File scripts/run-worker.ps1
```

Set secrets in the root `.env` file before first public start:

- `O2P_JWT_SECRET`
- `O2P_ADMIN_PASSWORD`
- metadata database credentials

The bootstrap admin is forced through password rotation flows and login throttling is enabled by default.

## Development Verification

```powershell
dotnet build src\O2P.slnx
cd web
cmd /c npm install
cmd /c npm run build
```

## Production Notes

- Configure Oracle and PostgreSQL connections in the UI; passwords are encrypted at rest through ASP.NET Data Protection.
- Bind each application to Oracle-Test, Oracle-Live, PG-Test, and PG-Live slots.
- Build manifests from discovered tables and launch jobs against a selected source/target environment.
- PG-Live launches require a typed confirmation phrase: `MIGRATE <application-name> LIVE`.
- Worker execution is chunk based. Chunks are claimable with database locks, leases are recovered, and a target-side `_o2p_chunk_log` fence prevents duplicate chunk loads after retries.

New to O2P? Follow the screen-by-screen [UI Guide](docs/UI-Guide.md). Setup, permissions and tuning are in [docs/User-Guide.md](docs/User-Guide.md).

See [docs/O2P_Production_Hardening_Plan.md](docs/O2P_Production_Hardening_Plan.md), [docs/DBA_Grants.md](docs/DBA_Grants.md), and [docs/Ops_Runbook.md](docs/Ops_Runbook.md) for the production checklist.

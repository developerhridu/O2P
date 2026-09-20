# User Guide

> Prefer a screen-by-screen walkthrough of the web UI? See the [UI Guide](UI-Guide.md).

This guide describes the behavior implemented in the current O2P codebase. O2P is an Oracle-to-PostgreSQL migration control plane: it scans Oracle tables, records which ones you picked, creates or reuses same-named PostgreSQL tables, copies data in batches, and compares source and destination row counts.

### The words the screens use

The interface was reworded to use plainer terms. If you used an earlier version, or you are reading
older notes, this is the mapping. The stored values, API routes and database are unchanged — only the
words on screen and in messages.

| You may have seen | It is now called | What it means |
| --- | --- | --- |
| `Application` | **Migration** | a named setup: its databases, table selections and runs |
| `Job Run`, `Job` | **Run** | one execution of a migration |
| `Manifest` | **Table Selection** | the tables you chose, with any row filters |
| `Manifest Builder` | **Select Tables** | the screen where you choose them |
| `Chunk` | **Batch** | one slice of a table, copied in parallel with others |
| `Discovery`, `Dictionary` | **Scan** | reading the source database to list its tables |
| `Preflight` | **Readiness check** | the checks run before a run starts |
| `Validation`, `Verification` | **Row count check** | comparing source and destination row counts |
| `Connection`, `Connection Profile` | **Database** | a saved database you can connect to |
| `Connection Slot` | **Database role** | shown as `Source · Test`, `Target · Live` |
| `Owner` | **Source schema** | the Oracle schema the tables live in |

Job and table states are shown as plain words too: `Queued` and `Pending` read as **Waiting**,
`Creating` as **Preparing**, `Loading` as **Copying**, `Validating` as **Checking rows**, `Completed`
as **Finished**, and `CompletedWithErrors` as **Finished with errors**. The values stored in the
database and returned by the API keep their original spellings, so scripts and integrations are
unaffected.

> **Important safety warning**
>
> A job against an existing target table is destructive. O2P drops selected PostgreSQL constraints, runs `TRUNCATE TABLE ... RESTART IDENTITY CASCADE`, loads the data, and then restores the captured constraints. Back up the target and test the complete process in non-production first.

## 1. Overview

### What O2P does

O2P provides:

- a React web UI for configuration and operations;
- a .NET 8 API for authentication, metadata, readiness checks, and job commands;
- a .NET 8 Worker that plans and executes migrations;
- a PostgreSQL metadata database that stores users, encrypted connection passwords, migrations, table selections, jobs, batches, metrics, and validation results.

### Supported migration direction

| Role | Implemented database |
|---|---|
| Source | Oracle |
| Target | PostgreSQL 14 or newer |
| O2P metadata store | PostgreSQL |

The current implementation does **not** provide PostgreSQL-to-Oracle, Oracle-to-Oracle, PostgreSQL-to-PostgreSQL, or arbitrary database-to-database migration.

### High-level flow

1. Start the metadata PostgreSQL database, API, Worker, and UI.
2. Sign in and change the bootstrap password if prompted.
3. Create and test an Oracle source profile.
4. Create and test a PostgreSQL target profile.
5. Create a migration and bind its Oracle/PostgreSQL Test or Live slots.
6. Discover an Oracle owner/schema.
7. Create and review a table selection of tables and optional row filters.
8. Create and launch a job.
9. The API runs mandatory readiness checks and queues the job.
10. The Worker creates missing target tables, or verifies and truncates existing ones, plans batches, copies rows, restores captured constraints, and validates row counts.
11. Review the job, table, batch, metric, and validation status in the UI.

### What is and is not migrated

Implemented:

- selected Oracle tables;
- selected table rows, optionally restricted by a per-table `WHERE` predicate;
- discovered columns and supported Oracle-to-PostgreSQL type mappings;
- `NULL` values, text, numeric values, timestamps, binary values, and selected Oracle LOB types;
- quoted, case-preserved schema, table, and column names.

Not implemented as an Oracle object migration:

- views, materialized views, procedures, functions, packages, triggers, grants, sequences, or synonyms;
- source primary keys, foreign keys, check constraints, or indexes;
- source identity/sequence semantics;
- change data capture or ongoing synchronization;
- schema-only or data-only run modes;
- generated CSV/JSON migration reports;
- row-level reject/salvage processing;
- checksum, hash, null-count, sum, min/max, or sampled-data validation.

For a newly created target table, O2P creates columns and nullability only. Although the model contains index and mapping-rule entities, the current discovery and execution paths do not apply source indexes or configurable mapping rules.

## 2. Prerequisites

### Software

Choose either the container or native development setup.

**Container setup**

- Docker Engine or Docker Desktop with Docker Compose v2.
- Enough disk space for the metadata volume, images, and logs.

**Native development setup**

- .NET 8 SDK.
- Node.js with npm.
- PostgreSQL for O2P metadata.
- A browser.

The runtime Oracle provider is included as a NuGet dependency; an Oracle client installation is not configured by this project.

### Database access

You need three logical connections:

1. **Metadata PostgreSQL** — used internally by the API and Worker.
2. **Source Oracle** — read by discovery, planning, copy, and validation.
3. **Target PostgreSQL** — modified by readiness check, DDL, truncate, COPY, constraint handling, and validation.

Do not confuse the metadata PostgreSQL database with the migration target.

### Oracle permissions

The source account must be able to:

- connect to Oracle;
- `SELECT` every source table included in the table selection;
- query `ALL_TABLES` and `ALL_TAB_COLUMNS`;
- query `SESSION_PRIVS`;
- query `ALL_CONSTRAINTS` and `ALL_CONS_COLUMNS` when numeric primary-key chunking is needed.

Additional dictionary access improves discovery statistics and ROWID/partition batch planning:

- `ALL_LOBS`, `ALL_EXTENTS`, `ALL_OBJECTS`, and `ALL_TAB_PARTITIONS`; or
- corresponding `DBA_*` views when `SELECT ANY DICTIONARY` is granted.

For **table sizes** specifically, grant `SELECT` on `DBA_SEGMENTS` (or `SELECT ANY DICTIONARY`). There is
no `ALL_SEGMENTS` view in Oracle — only `DBA_SEGMENTS` and `USER_SEGMENTS` — so without one of those, an
account can read exact sizes only for its own schema. Where neither is readable, O2P estimates the size
from `ALL_TABLES` statistics and marks it with a `~`.

If extent or primary-key metadata is unavailable, the planner may fall back to ROWID hashing or a whole-table/whole-partition read. The migration is intended to remain read-only on Oracle.

Example grants must be reviewed by an Oracle DBA and restricted to the required schemas:

```sql
GRANT CREATE SESSION TO O2P_SOURCE_USER;
GRANT SELECT ON SOURCE_OWNER.SOURCE_TABLE TO O2P_SOURCE_USER;
```

Broader example grants are described in `docs/DBA_Grants.md`, but least-privilege grants are recommended.

### PostgreSQL target permissions

The target account needs:

- `CONNECT` on the target database;
- `USAGE` and `CREATE` on the target schema;
- permission to create, insert into, and drop a probe table;
- permission to create the destination table and `_o2p_chunk_log`;
- permission to run binary `COPY FROM STDIN`;
- permission to truncate existing target tables;
- permission to drop and recreate applicable constraints on the target and referencing tables.

The target schema must already exist. O2P does not create it. The **Destination schema** field on the
Start-a-run dialog lists the schemas the account can use in the chosen destination database, reading
`pg_namespace` and `has_schema_privilege`, and marks each one with whether tables can be created in it.
A schema that is not listed can still be typed.

Example:

```sql
CREATE SCHEMA IF NOT EXISTS target_schema AUTHORIZATION O2P_TARGET_USER;
GRANT CONNECT ON DATABASE TARGET_DATABASE TO O2P_TARGET_USER;
GRANT USAGE, CREATE ON SCHEMA target_schema TO O2P_TARGET_USER;
```

Existing tables may require ownership or additional explicit privileges for `TRUNCATE` and `ALTER TABLE`.

### Network and firewall

The API and Worker host must be able to reach:

- the metadata PostgreSQL host and port;
- the source Oracle listener and service;
- the target PostgreSQL host and port.

Browser clients must be able to reach the UI and API/proxy. Typical defaults are:

| Service | Development/declared port |
|---|---:|
| UI declared by Docker Compose (full stack is not turnkey; see setup) | `3000` |
| Vite development UI | `5151` |
| API | `5000` |
| Metadata PostgreSQL exposed by Compose | `7936` |
| Oracle conventional default | `1521` |
| PostgreSQL conventional default | `5432` |

Open only the ports required by the deployment. Do not expose the metadata database publicly.

## 3. Project Setup

### Verified path: native development

The checked-in Compose file is useful for provisioning only the metadata PostgreSQL database:

```powershell
$env:O2P_METADATA_PASSWORD = "<STRONG_METADATA_PASSWORD>"
docker compose up -d metadata-pg
docker compose ps
docker compose logs -f metadata-pg
```

For that container, use this metadata connection in both API and Worker:

```text
Host=127.0.0.1;Port=7936;Database=nonoraclemigrationdb;Username=nonoraclemigrationdb;Password=<STRONG_METADATA_PASSWORD>
```

Alternatively, provision a separate PostgreSQL metadata database. Its login must be able to create/use schema `o2p`, apply the EF migrations, and perform ongoing CRUD on the created objects.

Open three terminals at the repository root.

**Terminal 1 — API**

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ConnectionStrings__MetadataDb = "Host=METADATA_HOST;Port=METADATA_PORT;Database=METADATA_DATABASE;Username=METADATA_USER;Password=<METADATA_PASSWORD>"
$env:JwtSettings__Secret = "<AT_LEAST_32_RANDOM_CHARACTERS>"
$env:BootstrapAdmin__Password = "<STRONG_BOOTSTRAP_PASSWORD>"
dotnet run --project src/O2P.Api --launch-profile http
```

Development Swagger is available at `http://localhost:5000/swagger`. The current API attempts to open it in the default browser after startup.

**Terminal 2 — Worker**

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
$env:ConnectionStrings__MetadataDb = "Host=METADATA_HOST;Port=METADATA_PORT;Database=METADATA_DATABASE;Username=METADATA_USER;Password=<METADATA_PASSWORD>"
dotnet run --project src/O2P.Worker
```

The API and Worker must use the exact same metadata connection string.

**Terminal 3 — UI**

```powershell
cd web
npm ci
npm run dev
```

Open `http://localhost:5151`. Vite proxies `/api` to `http://localhost:5000`.

The API applies EF Core metadata migrations and seeds the bootstrap administrator asynchronously. Wait for the API log message `Metadata DB migrate/seed completed` before signing in. The bootstrap account is forced to change its password.

Stop the optional metadata container without deleting its data:

```powershell
docker compose stop metadata-pg
```

To intentionally delete all O2P metadata, configuration, history, and Data Protection keys stored in that volume:

```powershell
docker compose down -v
```

### Full Docker Compose warning

Do **not** use `docker compose up --build` as a turnkey full-stack procedure with the files in their current state:

- Compose maps API host port `5000` to container port `80`, while the .NET 8 Linux image is not configured to listen on `80`;
- the nginx UI has no `/api` reverse-proxy configuration;
- the UI build does not receive `VITE_API_BASE_URL`;
- API/Worker readiness is not tied to a healthy metadata database;
- API, Worker, and UI file logs are not persisted.

The full Compose stack requires deployment changes outside the scope of this guide. The native API/Worker/UI procedure above is the verified source-development path.

### Native Windows published deployment

`deploy.ps1` publishes the API and Worker, builds the active `web` UI, starts the API/proxy/UI, optionally starts one Worker, and can create Windows Firewall rules. It requires an already reachable metadata PostgreSQL database.

Set an explicit metadata connection and safe secrets before running it:

```powershell
cd E:\DataFlow
$env:ConnectionStrings__MetadataDb = "Host=METADATA_HOST;Port=METADATA_PORT;Database=METADATA_DATABASE;Username=METADATA_USER;Password=<METADATA_PASSWORD>"
$env:O2P_JWT_SECRET = "<AT_LEAST_32_RANDOM_CHARACTERS>"
$env:O2P_ADMIN_PASSWORD = "<STRONG_BOOTSTRAP_PASSWORD>"
.\deploy.ps1 -ServerHost SERVER_HOST
```

Relevant parameters are `-UiPort` (default `3051`), `-ProxyPort` (default `3052`), `-ApiPort` (default `5050`), `-SkipFirewall`, `-SkipBuild`, `-NoWorker`, and `-WorkerOnly`.

Run the Worker on exactly one machine. The current Worker is not safe for a multi-node deployment: table preparation has no distributed claim, and any newly starting Worker requeues all `Running` batches, including work owned by another live Worker.

The script launches background processes, not Windows Services. API/proxy/UI do not have durable crash/reboot supervision; production operators must provide an appropriate service manager. The API launch script also binds to all interfaces, so restrict the API port with host firewall rules or bind it safely behind a reverse proxy.

### Build verification

```powershell
dotnet build src/O2P.slnx
cd web
npm ci
npm run build
```

### Configuration reference

ASP.NET Core environment variables use double underscores in place of JSON nesting.

| Setting | Required | Default/current behavior |
|---|---|---|
| `ConnectionStrings__MetadataDb` | Yes for API and Worker | Development JSON contains a local value; override it for every real environment |
| `JwtSettings__Secret` | Yes | Repository defaults are development placeholders and must be overridden |
| `JwtSettings__Issuer` | Yes | Loaded from API settings |
| `JwtSettings__Audience` | Yes | Loaded from API settings |
| `JwtSettings__ExpiryMinutes` | No | `20` minutes in current settings |
| `BootstrapAdmin__Username` | No | `admin` |
| `BootstrapAdmin__Email` | No | local internal address in settings |
| `BootstrapAdmin__Password` | Operationally required | If blank, code uses an insecure fallback and forces password change |
| `Security__RequireHttps` | No | `false` in repository settings |
| `Security__Cors__AllowedOrigins__N` | Required for remote UI origins | Local UI origins are listed in settings |
| `Concurrency__MaxOracleSessions` | No | Worker default `8` |
| `Concurrency__MaxChunkWorkers` | No | Worker default equals Oracle sessions; current JSON sets `8` |
| `Concurrency__ChunkTimeoutMinutes` | No | `20`, clamped to at least 1 |
| `VITE_API_BASE_URL` | Required for separately hosted static UI | Otherwise `/api/v1` |

The individual Windows scripts under `scripts/` expect pre-published output under `publish/api` and `publish/worker`; they are not source-development commands.

### Kubernetes warning

The checked-in Kubernetes table selections are templates, not a verified turnkey deployment. In particular, they currently use `ConnectionStrings__Metadata`, while the migration reads `ConnectionStrings__MetadataDb`, and their image/container-port assumptions must be aligned with the Dockerfiles. Correct and validate the table selections before production use.

## 4. Database Configuration

Open **Connections** in the UI. Only an Admin can create, update, or delete connection profiles.

### Connection fields

| Field | Required | Meaning |
|---|---|---|
| Profile Name | Yes | Unique human-readable name |
| Database Engine | Yes | `Oracle Database` for source or `PostgreSQL` for target |
| Host Address / IP | Yes | DNS name or IP reachable from the API and Worker hosts |
| Port | Yes | Listener port; UI defaults to `1521` for Oracle and `5432` for PostgreSQL |
| Service Name / SID | Yes for Oracle | Passed by the implementation as `HOST:PORT/SERVICE`; use a service name compatible with that syntax |
| Database Name | Yes for PostgreSQL | Target database name |
| Username | Yes | Database service account |
| Password | Yes when creating | Encrypted with ASP.NET Data Protection before storage; leave blank during edit to retain the existing password |
| Connection Options (JSON) | No | Stored in metadata, but **not consumed by current Oracle/Npgsql connection creation code** |

Do not rely on the Options JSON placeholder for SSL, pooling, or timeouts; those options are not applied by the current execution path.

### Safe Oracle example

| Field | Example |
|---|---|
| Profile Name | `Oracle Source Test` |
| Engine | `Oracle Database` |
| Host | `SOURCE_HOST` |
| Port | `SOURCE_PORT` |
| Service | `SOURCE_DATABASE` |
| Username | `SOURCE_USER` |
| Password | supplied securely at deployment time |

### Safe PostgreSQL example

| Field | Example |
|---|---|
| Profile Name | `Postgres Target Test` |
| Engine | `PostgreSQL` |
| Host | `TARGET_HOST` |
| Port | `TARGET_PORT` |
| Database | `TARGET_DATABASE` |
| Username | `TARGET_USER` |
| Password | supplied securely at deployment time |

### Test each profile

Click **Test** on the Connections page.

Oracle test results include:

- connectivity and latency;
- server version text;
- whether `DBA_TABLES` is queryable.

PostgreSQL test results include:

- connectivity and latency;
- server version;
- whether a temporary table can be created and dropped.

This profile test is useful but not sufficient for migration. Job launch performs a stronger target-schema readiness check.

### Credential handling

Connection passwords are encrypted in the metadata database using ASP.NET Data Protection, and API responses mask `SecretCiphertext`. Back up the metadata database, including the `o2p.dataprotection_keys` table, together. Losing or replacing those keys can make stored connection passwords undecryptable; re-enter the password in the connection profile if that occurs.

## 5. Migration Configuration

### Migrations and environment slots

An Migration groups connection bindings, table selections, and jobs for one system.

1. Open **Migrations**.
2. Select **New Migration**.
3. Enter a unique name and optional description.
4. Select **Manage**.
5. Assign the required slots:

| Slot | Database kind | Purpose |
|---|---|---|
| Oracle Test | Oracle | non-production source |
| Oracle Live | Oracle | production source |
| Postgres Test | PostgreSQL | non-production target |
| Postgres Live | PostgreSQL | production target |

A job selects one Oracle slot and one PostgreSQL slot. You do not have to bind unused slots.

### Discovery

Discovery uses an Oracle profile plus an owner/schema name. Owner and requested table names are normalized to uppercase.

Two supported UI paths are available:

- **Auto-Gen Table Selection** performs a full owner scan and creates a table selection immediately.
- **Custom Builder** lets you run a full **Refresh Dictionary** scan or paste specific table names.

A full scan replaces the cached discovery rows for that connection and owner. A targeted lookup replaces only the requested cached tables and leaves other cached tables intact.

Discovery records:

- table name and owner;
- row count from Oracle statistics, where they exist;
- table size, read from `DBA_SEGMENTS` or `USER_SEGMENTS` when the account can see them, otherwise estimated from statistics and marked `~`;
- LOB byte totals when `ALL_LOBS` is readable;
- partitioned and index-organized-table flags;
- columns, Oracle data types, lengths, precision, scale, nullability, and identity flag.

Figures may be missing or stale, because Oracle only records a row count once `DBMS_STATS` has gathered statistics for the table. A table with none shows **Unknown** rather than `0` — the two mean very different things, and a table that genuinely holds no rows is shown as `0`. **Sync counts & sizes** on the table-selection screen replaces Unknown with an exact `SELECT COUNT(*)` and the table's current size; each count reads the whole table on the source, so it is an explicit action rather than part of every scan. These are planning figures, not final validation.

The standalone **Discovery** navigation page is currently a visual placeholder and does not call the API. Use Migration → **Auto-Gen Table Selection** or **Custom Builder**.

### Table Selection options

In the Custom Builder, users can:

- include or exclude whole tables with the checkbox;
- remove tables from the table selection;
- search the current list;
- add specific table names, one per line or comma-separated;
- set a per-table Oracle filter predicate.

The filter must be only the predicate, for example:

```sql
CREATED_AT >= DATE '2026-01-01' AND STATUS = 'ACTIVE'
```

Do not include the word `WHERE`. The reader rejects semicolons, SQL comments, and common DDL/DML keywords. Filters are stored and later interpolated into source SQL, so only use DBA-reviewed, read-only predicates.

Current UI limitations:

- columns and generated PostgreSQL types are not editable in the builder;
- column exclusion is represented by the API model but has no current UI control;
- source PK flags are not discovered and are set to false;
- no wildcard, regular-expression, CSV upload, or schema-diff mode is implemented.

### Default type mappings

| Oracle type | PostgreSQL type |
|---|---|
| `VARCHAR2`, `NVARCHAR2` | `varchar(length)` when valid length is present, otherwise `text` |
| `CHAR`, `NCHAR` | `char(length)` when valid length is present, otherwise `text` |
| `CLOB`, `NCLOB`, `LONG` | `text` |
| `NUMBER(p,0)` | `smallint` for p≤4, `integer` for p≤9, `bigint` for p≤18, otherwise `numeric(p)` |
| `NUMBER(p,s)` | `numeric(p,s)` |
| unbounded `NUMBER` | `numeric` |
| `DATE` | `timestamp(0) without time zone` |
| `TIMESTAMP` | `timestamp without time zone` |
| `TIMESTAMP WITH TIME ZONE`, `TIMESTAMP WITH LOCAL TIME ZONE` | `timestamp with time zone` |
| `BLOB`, `BFILE`, `RAW`, `LONG RAW` | `bytea` |
| `BINARY_FLOAT` | `real` |
| `FLOAT`, `BINARY_DOUBLE` | `double precision` |
| `ROWID`, `UROWID` | `varchar(4000)` |
| `XMLTYPE` | `xml` |
| Oracle interval types | `interval` |
| Unknown type | `text` |

Review generated types before production. Unknown or provider-specific values falling back to `text` may still fail binary COPY if the Oracle driver returns a CLR value that Npgsql cannot write to that target type.

### Target schema and table behavior

At job launch:

- target schema defaults to `public`;
- target table name is always the source table name;
- identifiers are quoted and case-preserved;
- no `_mgN` suffix is allocated;
- a missing table is created with table selection columns;
- an existing table is **not altered** to match the table selection — no `CREATE`, no `ALTER` is issued for it;
- an existing table is checked for compatibility, then truncated before load.

**Compatibility check on an existing table.** Before anything is dropped or truncated, O2P reads the
target's real columns from `pg_attribute` and compares them to the table selection. If they cannot accept the
data, that one table fails with a message naming every problem, **nothing is altered, truncated or
loaded**, and the job's other tables carry on. Fix the target (or exclude the columns in the table selection)
and use **Retry failed**.

A table is blocked when:

- a table selection column is missing from the target (names are case-sensitive — Oracle yields `EMP_ID`,
  a hand-built table often has `emp_id`; the message names the real spelling);
- the type family differs (text vs numeric vs timestamp vs bytea);
- a fixed-width type differs at all, **widening included** — `integer` into `bigint` fails, because
  binary `COPY` carries no type OIDs and the bytes are written from the table selection type;
- the target is narrower (`varchar(100)` for a `varchar(255)` column, `numeric(8,2)` for
  `numeric(10,2)`, or a smaller scale, which PostgreSQL would round away silently);
- `timestamp` meets `timestamp with time zone` in either direction — `COPY` accepts this and
  silently reinterprets every value;
- `text` meets `bytea` — also accepted silently, storing corrupt bytes;
- the target has an extra `NOT NULL` column with no default, identity or generation expression.

Column *order* is never a problem: `COPY` names its columns explicitly. A target that is wider, or
looser about nulls, is fine.

### Chunking, batch size, and concurrency

There is no user-configurable batch-size option.

Implemented execution values:

- planner target: `16` batches per table, hard-coded;
- bounded in-memory channel: `1,000` rows;
- one PostgreSQL binary COPY transaction per batch;
- fixed worker-wide rate limiter: `50,000` rows/second with a `10,000`-row burst;
- Worker defaults: `8` Oracle sessions and `8` batch workers;
- batch timeout: `20` minutes by default;
- metrics sample interval: `2` seconds.

Configure effective concurrency in `src/O2P.Worker/appsettings.json`, `src/O2P.Worker/appsettings.Development.json`, or environment variables:

```powershell
$env:Concurrency__MaxOracleSessions = "4"
$env:Concurrency__MaxChunkWorkers = "4"
$env:Concurrency__ChunkTimeoutMinutes = "30"
```

The **Settings** screen stores values only in browser `localStorage`. It does not update the Worker, planner, rate limiter, validation, metrics interval, or retention behavior. Treat that screen as non-operational in the current version.

Migration `DefaultsJson` and Connection `OptionsJson` are also stored but not consumed by the Worker. The API accepts an `update_throttle` job command, but the Worker does not implement it. Effective runtime tuning is limited to the Worker configuration described above.

### Batch strategies

The Worker chooses automatically:

- non-IOT heap: extent-based ROWID ranges;
- partitioned table: partition lanes, with per-partition ROWID ranges when possible;
- IOT or fallback: ranges over a single-column numeric primary key;
- heap fallback: hashed ROWID buckets;
- final fallback: one whole table or partition batch.

Batch strategy is not user-selectable.

## 6. Migration Process

### 1. Configure the source

1. Open **Connections**.
2. Create an Oracle profile.
3. Click **Test**.
4. Resolve connectivity and catalog-access warnings before continuing.

### 2. Configure the target

1. Create a PostgreSQL profile.
2. Confirm PostgreSQL is version 14 or newer.
3. Ensure the intended schema already exists.
4. Click **Test**.
5. Independently verify the account can create, truncate, alter, and COPY into tables in that schema.

### 3. Create and bind a migration

1. Open **Migrations** → **New Migration**.
2. Open **Manage**.
3. Assign the Oracle source profile to `Oracle Test` or `Oracle Live`.
4. Assign the PostgreSQL target profile to `Postgres Test` or `Postgres Live`.

### 4. Discover and configure tables

For a quick full-schema table selection:

1. Select **Auto-Gen Table Selection**.
2. Choose the Oracle profile.
3. Enter the Oracle owner/schema.
4. Select **Generate**.

For controlled selection:

1. Select **Custom Builder**.
2. Choose the Oracle profile and owner.
3. Select **Refresh Dictionary**, or paste specific table names and select **Add Tables**.
4. Uncheck unwanted tables.
5. Add DBA-reviewed filter predicates where required.
6. Select **Save Table Selection**.

### 5. Review before launch

Verify:

- the table selection contains only intended tables;
- no selected table shows `NO COLUMNS`;
- estimated sizes are plausible;
- generated data types are compatible;
- filters return the intended source rows;
- the target schema and same-named tables are correct;
- every existing target table is backed up and approved for truncation;
- the Worker is running.

### 6. Start migration

1. Return to Migration **Manage**.
2. Select **Run Job** on the desired table selection.
3. Select the source and target slots.
4. Enter the target PostgreSQL schema.
5. Select **Launch Migration**.

The API:

1. creates the job in `Draft`;
2. verifies source and target slot bindings;
3. runs readiness check automatically;
4. rejects the launch if readiness check fails;
5. records a launch command and changes the job to `Queued`.

Readiness check verifies:

- Oracle connection and ability to query `SESSION_PRIVS`;
- PostgreSQL version 14+;
- PostgreSQL `USAGE` and `CREATE` privileges on the target schema;
- create/insert/drop of a probe table.

The Oracle readiness check currently treats successful access to `SESSION_PRIVS` as success; it does not enforce a nonzero count of particular privileges. A passing readiness check therefore does not replace a real test of every selected source table.

### 7. Monitor progress

The Worker changes states approximately as follows:

```text
Job: Draft → Queued → Running → Completed / CompletedWithErrors
Table: Pending → Creating → Planning → Loading → Validating → Completed / CompletedWithErrors / Failed
Chunk: Pending → Running → Done / Failed
```

Open **Runs** → **View Details**. See section 8 for all indicators.

### 8. Handle errors

- Expand a table to inspect its error message and failed batch tooltips.
- Review API and Worker logs.
- Use **Pause** to stop new batch claims; already running batches continue.
- Use **Cancel job** to request cancellation and constraint restoration.
- Do not use **Retry failed** as the default recovery after partial load failure; see section 11.

### 9. Confirm completion

A clean run requires:

- job status `Completed`;
- every table status `Completed`;
- every batch status `Done`;
- a passing row-count validation for every table;
- independent target sampling and migration-level checks.

`CompletedWithErrors` is not a successful migration. It means at least one table failed or row-count validation did not pass.

### Live target limitation

The API requires this exact confirmation phrase for a `pg_live` target:

```text
MIGRATE <application-name> LIVE
```

The current UI displays a different phrase and does not pass its input to the API. Therefore, launching a PostgreSQL Live job through the current UI does not work. In Development, use Swagger to:

1. create the job with `targetSlot` set to `pg_live`;
2. call `POST /api/v1/jobs/{id}/launch`;
3. send the exact API-required phrase in `confirmationPhrase`.

Do not bypass this gate by relabeling a production target as Test.

## 7. Example: Oracle Database A → PostgreSQL Database B

This example migrates selected tables from `SOURCE_OWNER` in Oracle to `target_schema` in PostgreSQL.

### Prepare access

Confirm:

```text
Oracle:     SOURCE_HOST:SOURCE_PORT/SOURCE_DATABASE
User:       SOURCE_USER
Owner:      SOURCE_OWNER

PostgreSQL: TARGET_HOST:TARGET_PORT/TARGET_DATABASE
User:       TARGET_USER
Schema:     target_schema
```

Create the target schema if required:

```sql
CREATE SCHEMA IF NOT EXISTS target_schema AUTHORIZATION TARGET_USER;
```

Back up any same-named target tables.

### Configure O2P

1. Create `Oracle A - Test`:
   - Engine: Oracle
   - Host: `SOURCE_HOST`
   - Port: `SOURCE_PORT`
   - Service: `SOURCE_DATABASE`
   - User: `SOURCE_USER`
   - Password: provide securely
2. Create `Postgres B - Test`:
   - Engine: PostgreSQL
   - Host: `TARGET_HOST`
   - Port: `TARGET_PORT`
   - Database: `TARGET_DATABASE`
   - User: `TARGET_USER`
   - Password: provide securely
3. Test both profiles.
4. Create a migration named `Database A to B`.
5. Bind:
   - Oracle Test → `Oracle A - Test`
   - Postgres Test → `Postgres B - Test`

### Build the table selection

1. Open **Custom Builder**.
2. Select `Oracle A - Test`.
3. Enter owner `SOURCE_OWNER`.
4. Paste:

```text
CUSTOMERS
ORDERS
ORDER_ITEMS
```

5. Select **Add Tables**.
6. Optionally set this predicate on `ORDERS`:

```sql
CREATED_AT >= DATE '2026-01-01'
```

7. Save the table selection.

### Run and verify

1. Select **Run Job**.
2. Source slot: Oracle Test.
3. Target slot: Postgres Test.
4. Target schema: `target_schema`.
5. Select **Launch Migration**.
6. Open the job details and wait for `Completed`.
7. Compare the built-in source and target counts.
8. In PostgreSQL, perform independent checks:

```sql
SELECT COUNT(*) FROM target_schema."CUSTOMERS";
SELECT COUNT(*) FROM target_schema."ORDERS";
SELECT COUNT(*) FROM target_schema."ORDER_ITEMS";
SELECT * FROM target_schema."CUSTOMERS" ORDER BY 1 FETCH FIRST 20 ROWS ONLY;
```

Because O2P preserves Oracle identifier case with quotes, actual table names may need double quotes exactly as shown by the job details.

## 8. Monitoring Migration

### UI

**Runs** refreshes every 5 seconds and shows:

- job ID, Migration, target schema, and status;
- Cancel and Retry actions where available.

The current **Dashboard** displays static zero values and is not a migration-monitoring source. Use Runs and Run Details.

**Run Details** refreshes every 2 seconds and shows:

- total rows rolled up from completed batches;
- active rows/second;
- active batch count;
- table status;
- completed batches and percentage;
- batch heatmap and error tooltips;
- table error message;
- source and target row-count validation.

Known display limitations:

- `BytesMigrated` is not populated by the current Worker, so transferred MB/KB remains zero;
- metrics `MbPerSecond` remains zero;
- the UI displays `/ 16` beside Active Workers even though effective Worker concurrency defaults to 8 and may be configured differently;
- there is no ETA.

### Logs

API and Worker use Serilog:

- console output;
- daily rolling files under the repository-level `logs` directory for native/published runs;
- `o2p-api-YYYYMMDD.log`;
- `o2p-worker-YYYYMMDD.log`;
- 14 retained files per process by configuration.

The full Compose stack is not turnkey, as described in section 3. If operators repair and use it, container file logs are not mounted by the current Compose file and should not be treated as durable. Add an operator-managed log sink or volume before production.

Useful Worker messages include claimed batches, successful row totals, timeouts, preparation failures, lease recovery, and validation failures.

### API

Authenticated endpoints used by the UI:

```text
GET /api/v1/jobs
GET /api/v1/jobs/{id}
GET /api/v1/jobs/{id}/metrics
GET /api/v1/jobs/{id}/validation
POST /api/v1/jobs/{id}/preflight
POST /api/v1/jobs/{id}/commands
```

Swagger is enabled only when `ASPNETCORE_ENVIRONMENT=Development`.

### Reports and audit records

Validation results and metrics are stored in metadata and displayed by the UI/API. The implementation does not generate downloadable CSV, JSON, PDF, or other report files. Metadata entities for run logs, row rejects, and additional audit data exist, but the migration path does not currently populate a complete end-user report.

## 9. Error Handling & Troubleshooting

### API starts but login/data pages fail

- **Symptom:** Swagger opens, but login or DB-backed endpoints fail.
- **Cause:** Metadata PostgreSQL is unavailable or initialization has not completed.
- **Diagnose:** Check API logs for repeated metadata migration attempts and test the metadata host/port.
- **Solution:** Start PostgreSQL, correct `ConnectionStrings__MetadataDb`, and wait for `Metadata DB migrate/seed completed`.

### Worker does not process a Queued job

- **Symptom:** Job remains `Queued`; no batches appear.
- **Cause:** Worker is stopped, uses a different metadata database, cannot connect to metadata, or cannot decrypt stored credentials.
- **Diagnose:** Check Worker startup/logs and compare its metadata connection string with the API.
- **Solution:** Start/restart Worker with the same metadata configuration and preserve the Data Protection keys.

### Job remains Running after table preparation fails

- **Symptom:** a table fails during Creating/Planning, but the parent job never reaches a terminal state.
- **Cause:** the current preparation-failure path marks the table Failed but does not always run parent-job completion evaluation when no batches were created.
- **Diagnose:** inspect table status/error and confirm that the table has no batch rows.
- **Solution:** correct the underlying issue, cancel the stuck job, and create a new full job. Do not assume the parent status will self-correct.

### Source connection refused or timed out

- **Symptom:** Connection test, discovery, planning, or batch fails with Oracle connection errors.
- **Cause:** Wrong host/port/service, listener/firewall issue, unavailable database, or exhausted Oracle sessions.
- **Diagnose:** Test from both API and Worker hosts; inspect Oracle listener and session limits; check error codes.
- **Solution:** Correct the profile/network and reduce `MaxOracleSessions`/`MaxChunkWorkers` if the source is constrained.

Planning and reading retry selected transient Oracle errors up to four attempts with exponential delays. Authentication and other non-transient errors fail immediately.

### Authentication failure or account lock

- **Symptom:** `Invalid credentials`, disabled-account message, HTTP 423, or UI returns to login.
- **Cause:** Wrong password, inactive account, five failed sign-ins, expired 20-minute token, password reset, or changed security stamp.
- **Diagnose:** Ask an administrator to inspect the Users page and API logs.
- **Solution:** use the correct credential, unlock/reactivate the account, or reset the password. Lockout duration defaults to 15 minutes.

### Discovery returns no tables

- **Symptom:** zero tables or requested table not found.
- **Cause:** wrong owner, missing `SELECT`/catalog visibility, incorrect table spelling, or table is a nested/IOT overflow object intentionally excluded.
- **Diagnose:** query `ALL_TABLES` and `ALL_TAB_COLUMNS` as `SOURCE_USER`.
- **Solution:** correct the owner/table names and grants; rerun full or targeted discovery.

### `NO COLUMNS` in the table selection

- **Symptom:** selected table has no discovered columns; target DDL will be invalid.
- **Cause:** column catalog access failed or stale/incomplete discovery data.
- **Diagnose:** check discovery response/logs and `ALL_TAB_COLUMNS` access.
- **Solution:** fix access and refresh discovery. Do not launch that table.

### Readiness check fails

- **Symptom:** launch returns `Preflight check failed`.
- **Cause:** Oracle connectivity/catalog query failure, PostgreSQL older than 14, missing schema privileges, absent schema, or failed probe DDL/DML.
- **Diagnose:** read the returned readiness check details and API logs.
- **Solution:** correct access, create the target schema, upgrade PostgreSQL, or use an appropriately privileged target account.

### Existing target schema/object mismatch

- **Symptom:** binary COPY reports missing column, type mismatch, null violation, or wrong column count.
- **Cause:** the table already exists and `CREATE TABLE IF NOT EXISTS` did not reconcile it.
- **Diagnose:** compare the table selection with PostgreSQL `information_schema.columns`, including exact quoted names and order.
- **Solution:** back up and manually recreate/align the target table, then start a new full job.

### Data type conversion or binary format error

- **Symptom:** Npgsql binary COPY error such as incompatible CLR/PostgreSQL type, overflow, invalid XML, or incorrect binary format.
- **Cause:** source value is incompatible with the generated type; numeric precision exceeds the selected integer; unknown Oracle type fell back to text; unsupported provider value.
- **Diagnose:** identify the failed table/batch and inspect source type/value ranges.
- **Solution:** adjust the target design/table selection through an approved API-level customization or preprocess the source. The current UI has no type-override control.

### Nullability violation

- **Symptom:** COPY fails with a NOT NULL violation.
- **Cause:** Oracle metadata says non-nullable but the reader normalizes empty strings and strings containing only NUL characters to PostgreSQL `NULL`, or the existing target is stricter.
- **Diagnose:** inspect source values and target nullability.
- **Solution:** clean/transform source data or use a compatible target definition.

### Constraint violation or restore failure

- **Symptom:** table becomes `Failed` or `CompletedWithErrors`; message mentions constraint restore.
- **Cause:** existing standalone data/dependencies, privileges, concurrent target changes, or loaded data violates a restored constraint.
- **Diagnose:** inspect Worker logs and PostgreSQL constraints/dependencies.
- **Solution:** stop concurrent target writes, repair data/schema, verify constraints, and perform a fresh full rerun.

O2P snapshots and temporarily drops local PK, UNIQUE, FK, CHECK constraints and inbound FKs. It does not snapshot/drop standalone indexes or triggers.

### Batch timeout

- **Symptom:** batch fails with `Chunk timed out ... stall watchdog`.
- **Cause:** slow Oracle query, LOB transfer, blocked PostgreSQL, network delay, or too-short timeout.
- **Diagnose:** inspect Oracle/PostgreSQL activity and Worker logs.
- **Solution:** remove the bottleneck, lower concurrency, or increase `Concurrency__ChunkTimeoutMinutes`, then use the safe recovery procedure in section 11.

Oracle commands also use a 600-second command timeout; PostgreSQL writer commands use 600 seconds.

### Failed table filter

- **Symptom:** `manifest WHERE clause contains unsupported SQL` or Oracle SQL error.
- **Cause:** filter contains a forbidden token, includes `WHERE`, or uses invalid Oracle syntax.
- **Diagnose:** test the predicate in a read-only `SELECT` as the source user.
- **Solution:** use only the predicate and remove comments, semicolons, and DML/DDL.

### Row-count mismatch

- **Symptom:** table is `CompletedWithErrors`.
- **Cause:** partial/missing target rows, concurrent source changes, trigger effects, retry inconsistency, wrong existing schema, or filter/value behavior.
- **Diagnose:** compare filtered Oracle count with quoted PostgreSQL count and inspect all batch statuses.
- **Solution:** freeze or reconcile source changes, repair the target, and run a new full job.

### “Batch/parameter limit” concerns

O2P does not build multi-row parameterized INSERT batches. It streams rows through a bounded channel into binary COPY, so conventional SQL parameter-count limits do not apply. Memory or transaction pressure is controlled primarily by batch size/strategy, row width, LOB behavior, concurrency, and the 1,000-row channel.

## 10. Data Validation

### Built-in validation

After all batches for a table are done, O2P:

1. counts rows in the Oracle source table using the table selection filter, if present;
2. counts all rows in the target table;
3. stores both values;
4. marks validation passed only when counts match.

Results appear under **Post-Migration Verification** on Run Details.

This is row-count validation only. Equal counts do not prove equal values.

### Practical verification checklist

1. Confirm job `Completed`, not `CompletedWithErrors`.
2. Confirm every table `Completed`.
3. Confirm every batch is `Done`.
4. Confirm every built-in row-count result passes.
5. Run independent source counts:

```sql
SELECT COUNT(*) FROM SOURCE_OWNER.SOURCE_TABLE;
```

For filtered tables, apply the exact table selection predicate.

6. Run target counts with exact quoted names:

```sql
SELECT COUNT(*) FROM target_schema."SOURCE_TABLE";
```

7. Sample deterministic business keys and important fields on both sides.
8. Check nulls, numeric totals, date boundaries, maximum string lengths, and LOB sizes using DBA-approved SQL.
9. Run migration-level reports or read-only smoke tests against the target.
10. Preserve job details, validation results, and logs as migration evidence.

There is no built-in failed-record report because the current COPY transaction fails the whole batch rather than recording individual rejected rows.

### Concurrent-source warning

O2P does not establish one database-wide Oracle snapshot across all batches and tables. If source rows are inserted, updated, or deleted during migration, batches and final row counts can observe different points in time. Plan a migration freeze or another source-consistency strategy before cutover.

## 11. Re-running / Resuming Migration

### Worker interruption

Each batch is claimed in metadata with:

- a Worker ID;
- a 10-minute lease;
- a heartbeat every 4 minutes;
- an attempt count.

Expired leases are returned to `Pending`. When a Worker starts, it also requeues **all** metadata batches left in `Running`, without checking lease owner or expiry. Run only one Worker process; starting another Worker can cause active work to be requeued.

The target writes one batch in a PostgreSQL transaction that includes a marker in:

```text
<target-schema>._o2p_chunk_log
```

The marker key is `(job_run_id, table_run_id, chunk_index)`. If a committed batch is retried, the duplicate marker prevents a second COPY, reducing duplicate-row risk after a Worker/metadata failure.

### Pause and resume

- **Pause:** job becomes `Paused`; no new batches match the claim query. Already running batches are allowed to finish.
- **Resume:** job returns to `Running`; pending batches can be claimed.

This is the normal safe continuation mechanism for an intentionally paused, otherwise healthy job.

### Cancel

Cancel marks the job, active tables, and pending/running batches as cancelled and attempts to restore constraints. Target tables are not dropped. Cancellation may truncate partially loaded target tables as part of constraint restoration.

Cancellation changes metadata but does not signal the cancellation token of an already executing Oracle read/PostgreSQL COPY. An active batch can continue and commit target rows after cancellation was requested, even though its final metadata update will not mark the cancelled batch Done. Treat a cancelled target as indeterminate and perform a fresh full job after inspection.

### Retry failed: current limitation

The UI/API can requeue failed or cancelled batches and failed/cancelled tables. However, when any batch fails, the current failure path truncates the target table before restoring constraints. `retry_failed` then requeues failed batches but does not requeue batches already marked `Done`.

Consequences:

- previously successful batch rows may have been removed by the truncate;
- retrying only failed batches can leave the target incomplete;
- row-count validation should detect the mismatch, but Retry is not a reliable full recovery mechanism for partial table failure.

**Recommended recovery after a partial failure**

1. Stop writes to the target.
2. Record the job and error details.
3. Verify constraints were restored.
4. Correct the source, target, network, permission, or type issue.
5. Create and launch a **new job** from the same reviewed table selection.
6. Allow the new job to truncate and reload the whole selected target table set.
7. Re-run all validation.

### New job/rerun behavior

A new job reuses the same source table names on the target. Existing destination tables are truncated and reloaded; they are not appended to. This avoids normal duplicate accumulation but overwrites existing target data.

The target batch fence is per job/table-run/batch, so it does not cause a new job to skip old rows.

### Precautions

- Never manually delete fence rows for an active job.
- Do not run two jobs against the same target tables concurrently.
- Run only one Worker process against a metadata database.
- Do not modify target tables or constraints while a job is active.
- Preserve metadata and Data Protection keys during restarts.
- Prefer a fresh full job after any uncertain partial failure.

## 12. Production Migration Considerations

### Before migration

- Back up the target database and test restore procedures.
- Test with Oracle Test → Postgres Test first.
- Rehearse the exact table selection, target schema, permissions, and validation plan.
- Review every existing target table because it will be truncated.
- Confirm disk for target data, indexes, WAL, temporary work, backups, and metadata.
- Confirm stable low-latency connectivity from every Worker host.
- Verify source session capacity and PostgreSQL connection capacity.
- Inventory dependencies not migrated by O2P: sequences, triggers, grants, code, views, and standalone indexes.
- Establish a rollback and cutover plan.

### Source consistency and downtime

The tool is a bulk snapshot copy, not CDC. It does not capture changes made after a batch is read. For a consistent cutover, arrange a migration write freeze, a DBA-managed source-consistency mechanism, or a separate reconciliation process. O2P itself does not schedule downtime or delta sync.

### Load tuning

Start conservatively:

```text
Concurrency__MaxOracleSessions=2–4
Concurrency__MaxChunkWorkers=2–4
```

Increase only after observing:

- Oracle session and I/O pressure;
- network throughput;
- PostgreSQL CPU, I/O, WAL, locks, and connection use;
- Worker timeouts and rows/second.

The fixed 50,000-row/second limiter is row-based, not byte-based. Wide rows and LOBs can still generate heavy bandwidth and target load.

### Large and LOB-heavy tables

- LOB tables use smaller Oracle fetch buffers and limited initial LOB prefetch.
- Each batch is one target transaction; ensure WAL and storage can accommodate it.
- ROWID-hash fallback performs multiple full source-table scans—up to the hard-coded 16 buckets—and can be expensive.
- Tables falling back to one batch cannot use parallel batch workers for that table.
- Increase timeout only after diagnosing whether the operation is progressing.

### Target constraints and dependencies

The Worker drops and restores selected existing constraints, including inbound foreign keys. This requires broad DDL privileges and creates a window in which constraints are absent. Standalone indexes and triggers remain and may slow or alter loads.

New target tables do not receive source PKs, indexes, foreign keys, sequences, or triggers. Create and validate those objects separately after data validation.

### Security

- Replace all development secrets.
- Serve UI/API over TLS behind a trusted reverse proxy.
- Restrict metadata, source, and target networks.
- Use separate least-privilege service accounts.
- Limit Admin access.
- Rotate the bootstrap password immediately.
- Back up and protect metadata/Data Protection keys.
- Do not put secrets in source-controlled JSON, shell history, logs, table selections, or screenshots.

Current hardening limitations must be resolved before exposure:

- Data Protection keys and encrypted connection credentials are stored in the same metadata database, and the keys have no external key-encryption protection.
- `UsersController` currently lacks server-side Admin-role enforcement; any authenticated API caller can reach user-management endpoints even though the UI hides them. Network isolation is not an adequate long-term fix.
- Repository defaults do not provide TLS termination or production health/readiness endpoints.
- CORS and forwarded-proxy trust must be narrowed to the actual deployment.

### Operational readiness

- Keep API and Worker clocks synchronized.
- Run exactly one Worker; current preparation and startup recovery are not multi-node safe.
- Persist and centralize logs.
- Alert when jobs remain Queued/Running without progress.
- Do not depend on the Settings UI for runtime tuning.
- Validate Kubernetes table selections and environment names before deployment.
- Run a representative performance test and a failure/restart rehearsal.
- Obtain business and DBA sign-off on row counts and data samples before cutover.

## 13. FAQ

### Can O2P migrate from any database to any other database?

No. The implemented path is Oracle source to PostgreSQL target.

### Can I run the complete project with the checked-in Docker Compose file?

Not without correcting its API container port and UI-to-API routing. The verified development path is native API/Worker/UI, optionally using `docker compose up -d metadata-pg` for only the metadata database.

### Can I run multiple Workers for higher throughput?

No. Current table preparation and startup batch recovery are not multi-node safe. Run exactly one Worker and tune its process-local concurrency settings.

### Does it migrate an entire Oracle database automatically?

No. It discovers tables for a selected owner and migrates only tables included in a saved table selection.

### Can I migrate only selected rows?

Yes. Enter an Oracle predicate in a table's Filter field. The same filter is used for source row-count validation.

### Can I migrate only selected columns?

The backend model supports an exclusion flag, but the current UI does not expose column selection. Normal UI-generated table selections include every discovered column.

### Can I change a generated data type?

Not through the current UI. Review type compatibility before production; an API-level table selection edit would require careful operator validation.

### Is there a batch-size setting?

No. Data streams through a 1,000-row bounded channel into one binary COPY transaction per batch.

### Does the Settings page tune active migrations?

No. It writes only browser-local values. Configure the Worker through appsettings or `Concurrency__...` environment variables.

### Will O2P append to an existing target table?

No. It truncates the same-named existing target table and reloads it.

### Does O2P create the PostgreSQL schema?

No. Create the schema and grant access before launch.

### Does it recreate Oracle constraints and indexes?

No. New target tables contain columns and nullability only. Existing PostgreSQL constraints are temporarily captured, dropped, and restored; that is not source-object migration.

### Are retries duplicate-safe?

The transactional target batch fence reduces duplicate-row risk for committed batches retried within the same job. Nevertheless, the current partial-failure recovery can truncate successful batch rows, so use a new full job after a partial table failure.

### Can I pause without losing completed work?

Yes. Pause stops new claims while current batches finish; Resume continues pending batches.

### Why is transferred MB always zero?

The current Worker does not populate byte counters or MB/second metrics.

### Why did an equal row count still produce bad data?

Row-count equality checks quantity, not value equality. Perform deterministic sampling and business validation.

### Can source data change during migration?

It can, but O2P does not provide a database-wide consistent snapshot or CDC. Changes can make the target inconsistent; use an operational freeze or external reconciliation strategy.

### Why can I not launch the Live target from the UI?

The current UI and API confirmation phrases are inconsistent, and the UI does not pass the phrase to the launch call. Use the Development Swagger/API with the exact server-required phrase until the migration code is corrected.

### Where are passwords stored?

Database passwords are encrypted in the metadata PostgreSQL database using ASP.NET Data Protection. Protect and back up its key table with the metadata.

### Where do I investigate a failed migration?

Start with Run Details, expand the failed table, inspect batch tooltips, then review `o2p-worker-*.log`, `o2p-api-*.log`, and the Oracle/PostgreSQL server logs.

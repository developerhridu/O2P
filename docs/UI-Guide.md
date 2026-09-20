# O2P Migration Studio — UI Guide

**Migrate an Oracle schema to PostgreSQL, one screen at a time.**

This guide walks you through the O2P web interface from the sign-in page to a verified migration. You do not need to call any API or read any code. Every button, field and message quoted here matches the current UI.

> Looking for installation, DBA grants, configuration keys or deployment? See [User-Guide.md](User-Guide.md), [DBA_Grants.md](DBA_Grants.md) and [Ops_Runbook.md](Ops_Runbook.md). This guide starts after the system is running.

## Contents

1. [What you will do](#1-what-you-will-do)
2. [Before you start](#2-before-you-start)
3. [Sign in and change your password](#3-sign-in-and-change-your-password)
4. [Tour of the screen](#4-tour-of-the-screen)
5. [Step 1 — Create users (Admin, optional)](#5-step-1--create-users-admin-optional)
6. [Step 2 — Add and test connections](#6-step-2--add-and-test-connections)
7. [Step 3 — Create an application and bind connections](#7-step-3--create-an-application-and-bind-connections)
8. [Step 4 — Build the manifest (choose tables)](#8-step-4--build-the-manifest-choose-tables)
9. [Step 5 — Launch the migration job](#9-step-5--launch-the-migration-job)
10. [Step 6 — Monitor the job](#10-step-6--monitor-the-job)
11. [Step 7 — Verify the result](#11-step-7--verify-the-result)
12. [Step 8 — Fix problems and re-run](#12-step-8--fix-problems-and-re-run)
13. [Worked example: migrate the HR schema](#13-worked-example-migrate-the-hr-schema)
14. [Troubleshooting by message](#14-troubleshooting-by-message)
15. [What O2P does not migrate](#15-what-o2p-does-not-migrate)
16. [FAQ and glossary](#16-faq-and-glossary)

---

## 1. What you will do

```
  ┌────────┐   ┌────────────┐   ┌─────────────┐   ┌──────────┐
  │ Sign in│──▶│ Connections│──▶│ Application │──▶│ Manifest │
  │        │   │ (Oracle +  │   │ + bind      │   │ (tables) │
  │        │   │  Postgres) │   │ 4 slots     │   │          │
  └────────┘   └────────────┘   └─────────────┘   └────┬─────┘
                                                       │
  ┌────────┐   ┌────────────┐   ┌─────────────┐        ▼
  │ Fix /  │◀──│ Verify     │◀──│ Monitor    │◀──┌──────────┐
  │ re-run │   │ row counts │   │ Job Details │   │ Launch   │
  └────────┘   └────────────┘   └─────────────┘   │ job      │
                                                  └──────────┘
```

| Guide section | Task | Screen | Who can do it |
|---|---|---|---|
| [3](#3-sign-in-and-change-your-password) | Sign in, set a new password | Login, Update Password | Everyone |
| [5](#5-step-1--create-users-admin-optional) | Create users (optional) | Users | Admin |
| [6](#6-step-2--add-and-test-connections) | Add the Oracle source and PostgreSQL target, click **Test** | Connections | Admin (create/edit/delete), everyone (Test) |
| [7](#7-step-3--create-an-application-and-bind-connections) | Create an application and bind connection slots | Applications | Admin or Operator |
| [8](#8-step-4--build-the-manifest-choose-tables) | Pick the tables to copy | Application → manifest tools | Admin or Operator |
| [9](#9-step-5--launch-the-migration-job) | Launch the job | Application → **Run Job** | Admin or Operator |
| [10](#10-step-6--monitor-the-job) | Watch progress | Job Runs, Job Details | Everyone (controls: Admin or Operator) |
| [11](#11-step-7--verify-the-result) | Check row counts | Job Details | Everyone |

### What the migration does — in one paragraph

For each table in your manifest, O2P creates the PostgreSQL table if it does not exist (columns and NULL/NOT NULL only), splits the Oracle table into up to 16 chunks, copies the chunks in parallel using PostgreSQL binary `COPY`, and compares the Oracle row count with the PostgreSQL row count at the end.

> **Warning — destructive to existing target tables.** If a table with the same name already exists in the target schema, O2P **drops its PK/UNIQUE/FK/CHECK constraints, runs `TRUNCATE TABLE … RESTART IDENTITY CASCADE`, loads the data and then re-creates the constraints.** Existing rows are deleted. Take a backup and rehearse against a non-production target first.

---

## 2. Before you start

Tick every box before opening the UI.

- [ ] **The system is running**: API, **Worker** and UI. If the Worker is not running, jobs stay `Queued` forever. Exactly one Worker must run.
- [ ] **You know the UI address.**

  | Deployment | Address |
  |---|---|
  | Windows published deployment (`deploy.ps1`) | `http://<server-ip>:3051` |
  | Developer machine (`npm run dev`) | `http://localhost:5151` |
  | Docker Compose UI | `http://localhost:3000` (see the warning in [User-Guide.md](User-Guide.md#full-docker-compose-warning)) |

- [ ] **Oracle account** with `CREATE SESSION` and `SELECT` on every table you want to migrate, plus read access to the `ALL_*` dictionary views. See [User-Guide.md §2](User-Guide.md#oracle-permissions).
- [ ] **PostgreSQL account and database** (version 14 or newer). The **target schema must already exist** (for example `public`, or a schema you created). O2P does not create schemas. The account needs `USAGE` and `CREATE` on it, and ownership of any existing target tables (needed for `TRUNCATE` and constraint changes).
- [ ] **A backup** of the PostgreSQL target if it already holds data.
- [ ] **Network access** from the API/Worker host to both databases. The browser does *not* talk to the databases directly.
- [ ] **Your role** — this decides which buttons work:

| Action | Admin | Operator | Viewer |
|---|:---:|:---:|:---:|
| View connections, applications, manifests, jobs, validation | ✅ | ✅ | ✅ |
| **Test** a connection | ✅ | ✅ | ✅ |
| Create / edit / delete a connection | ✅ | ❌ | ❌ |
| Create an application, bind slots, build manifests, launch jobs | ✅ | ✅ | ❌ |
| Pause / Resume / Retry / Cancel a job | ✅ | ✅ | ❌ (buttons hidden) |
| Delete an application | ✅ | ❌ | ❌ |
| **Cancel All & Restart Worker** | ✅ | ❌ | ❌ |
| **Users** page (create users, reset passwords) | ✅ | ❌ | ❌ |

> **Tip.** A Viewer still *sees* **New Connection**, **Edit** and **Delete**, and some failed actions (creating or deleting an application, deleting a connection) fail **silently** with no message on screen. If a button does nothing, check your role first (bottom-left of the sidebar).

---

## 3. Sign in and change your password

### 3.1 Sign in

1. Open the UI address in your browser. You land on **O2P Migration Studio — Sign in to manage migrations**.
2. Enter **Username** (pre-filled with `admin`) and **Password**.
3. Click **Sign In**.

```
        ┌──────────────────────────────────────┐
        │        O2P Migration Studio          │
        │   Sign in to manage migrations       │
        │                                      │
        │  Username  [ admin               ]   │
        │  Password  [ ••••••••••••        ]   │
        │                                      │
        │            [   Sign In   ]           │
        └──────────────────────────────────────┘
```

**First login (fresh install):** username `admin`, password `AdminPassword123!` — unless your administrator set another bootstrap password. Change it immediately (next section).

> Wait for the API to finish preparing its database (log line `Metadata DB migrate/seed completed`) before the very first sign-in, otherwise login fails.

| Message | Meaning | What to do |
|---|---|---|
| `Invalid credentials` | Wrong username/password (also shown if you hit the login rate limit) | Retype. Wait 5 minutes if you tried more than 5 times |
| `This account is disabled.` | An Admin deactivated the account | Ask an Admin |
| `This account is temporarily locked due to repeated failed sign-in attempts.` | 5 failed attempts → locked for 15 minutes | Wait, or ask an Admin to click **Unlock** on the Users page |
| `Cannot reach the API. Check that O2P.Api is running and try again.` | API is down, or the UI cannot reach it | Tell your administrator |

Your session lasts **20 minutes**. There is no refresh; when it expires you are sent back to the sign-in page and must sign in again. Do this before starting long editing sessions, such as a large manifest.

### 3.2 Update Password (forced on first login)

The bootstrap `admin` account must change its password before it can do anything else. You see **Update Password — This account must change its password before continuing.**

1. Enter **Current Password**.
2. Enter **New Password** and **Confirm New Password**.
3. Click **Update Password**.

The password must have **at least 12 characters, an upper-case letter, a lower-case letter, a digit and a symbol, and at least 4 different characters**. If the password is too weak the page shows a generic `Password change failed.` — pick a longer, more varied one. A mismatch shows `New password and confirmation do not match.` On success you see `Password updated successfully.` and land on the Dashboard.

There is no "forgot password" link. An Admin resets passwords on the **Users** page (or an operator uses the `tools/admin-password-reset` command-line tool).

You can change your password any time from the sidebar: **Change Password**.

---

## 4. Tour of the screen

```
┌────────────────────┬──────────────────────────────────────────────┐
│ O2P Migration      │                                              │
│ Studio             │                                              │
│ Oracle to Postgres │            (page content)                    │
│ Control Center     │                                              │
│                    │                                              │
│ ▸ Dashboard        │                                              │
│ ▸ Connections      │                                              │
│ ▸ Applications     │                                              │
│ ▸ Job Runs         │                                              │
│ ▸ Users   (Admin)  │                                              │
│ ▸ Settings         │                                              │
│                    │                                              │
│ Your Name          │                                              │
│ Admin              │                                              │
│ [Change Password]  │                                              │
│ [Sign Out]         │                                              │
└────────────────────┴──────────────────────────────────────────────┘
```

| Menu item | Use it for |
|---|---|
| **Dashboard** | A static landing page. The tiles (*Active Jobs*, *Total Migrated (GB)*, *Global Throughput*) always show zero — **do not use it to monitor migrations.** Use **Job Runs** instead. |
| **Connections** | Oracle source and PostgreSQL target profiles |
| **Applications** | Groups connections, manifests and jobs for one system. **This is where you build manifests and launch jobs.** |
| **Job Runs** | List of every migration job, refreshed every 5 seconds |
| **Users** | Admin only: user accounts and roles |
| **Settings** | Stored **only in your browser**; it does **not** change how migrations run. You can ignore it. |

There is a `/discovery` page ("Database Discovery") in the code, but it is not in the menu and is not functional. Table discovery is built into the Application page and the Manifest Builder (see [Step 4](#8-step-4--build-the-manifest-choose-tables)).

The active menu item is highlighted. Under your name you see your display name and roles.

---

## 5. Step 1 — Create users (Admin, optional)

Skip this if you work alone as `admin`. For a team, create separate accounts so you know who launched what.

1. Click **Users** in the sidebar (Admin only; others see `Admin role required.`).
2. Click **New User**.
3. Fill in the **Create User** dialog:

| Field | Notes |
|---|---|
| **Username** | Cannot be changed later |
| **Display Name** | Shown in the sidebar |
| **Email** | Required, must be unique |
| **Temporary Password** | 12+ characters, same rules as above |
| **Roles** | Tick **Admin**, **Operator** and/or **Viewer** (at least one; default Viewer) |
| **Active** | Untick to disable the account |
| **Force password change** | Recommended: user must set their own password at first sign-in |

4. Click **Save User**.

The table shows **Status** (`Active` / `Disabled`, and `Locked until <time>`), **Password** (`Change required` / `Current`) and **Last Login**.

| Row action | What it does |
|---|---|
| **Edit** | Change display name, email, roles, active flag |
| **Reset Password** | Opens *New Temporary Password* with the checkbox *Require password change on next login* |
| **Unlock** | Appears only for locked accounts |

You cannot disable your own account, and every user must keep at least one role.

Which role to give: **Operator** for people who run migrations, **Viewer** for people who only watch, **Admin** for the person who manages connections and users.

---

## 6. Step 2 — Add and test connections

You need **two** profiles: the Oracle **source** and the PostgreSQL **target**. (The PostgreSQL that stores O2P's own data is separate and is not configured here.)

Open **Connections**. The page is titled **Connection Profiles** and lists profiles with columns *Name, Kind, Host, Database/Service, Status / Info, Actions*. When empty it says `No connection profiles created yet. Click "New Connection" to add one.`

### 6.1 Create a profile (Admin)

1. Click **New Connection**. The **Create Connection Profile** dialog opens.

```
┌─ Create Connection Profile ─────────────────────────────┐
│ Profile Name        [ Oracle HR Test                  ] │
│ Database Engine     [ Oracle Database              ▾ ]  │
│ Host Address / IP   [ SOURCE_HOST                     ] │
│ Port                [ 1521 ]                            │
│ Service Name / SID  [ SOURCE_SERVICE                  ] │
│ Username            [ SOURCE_USER                     ] │
│ Password            [ ••••••••                        ] │
│ Connection Options (JSON - Optional)  [ leave empty ]   │
│                                  [ Cancel ] [Save Profile]
└─────────────────────────────────────────────────────────┘
```

2. Fill the fields:

| Field | Oracle source | PostgreSQL target |
|---|---|---|
| **Profile Name** (required, unique) | e.g. `Oracle HR Test` | e.g. `Postgres Target Test` |
| **Database Engine** | `Oracle Database` | `PostgreSQL` |
| **Host Address / IP** | Server name or IP reachable **from the API/Worker host** | same |
| **Port** | `1521` (auto-filled) | `5432` (auto-filled) |
| **Service Name / SID** (Oracle) or **Database Name** (PostgreSQL) | Oracle **service name**, e.g. `ORCL`. A service name is safest; a SID may not work | Database name, e.g. `postgres` |
| **Username** | Account with `SELECT` on the tables | Account with rights on the target schema |
| **Password** | Required when creating. Stored **encrypted**; never shown again | same |
| **Connection Options (JSON)** | **Leave empty.** It is stored but not used — you cannot set SSL, pool size or timeouts here | same |

> The label of the fourth field changes with the engine. Choosing the engine also switches the port between 1521 and 5432 if you had not typed a custom port.

3. Click **Save Profile**. If the name is taken you get `A connection profile named "<name>" already exists. Choose a different name.` If you are not Admin you get `Failed to save connection: You do not have permission to do this (Admin role required).`

### 6.2 Test every profile

Click **Test** on the row. The **Status / Info** column changes:

| Status | Meaning |
|---|---|
| Untested (grey) | Not tested yet |
| Testing… (spinner) | In progress (5-second timeout) |
| **Online (NNms)** (green) + version | Connected. Shows e.g. `Oracle Database …` or `PostgreSQL …` |
| **Failed** (red) + text | `Connection failed: <reason>` or `Network error communicating with the API.` |

Fix a Failed result before continuing (wrong host/port, firewall, wrong service name, wrong password, listener down).

> The test proves you can log in. It does **not** prove the account can read your tables or create tables in your target schema. Those are checked later, when you launch the job (**preflight**).

### 6.3 Edit and delete

- **Edit** reopens the dialog titled `Edit Connection: <name>`. Leave **Password** blank to keep the current one.
- **Delete** asks `Are you sure you want to delete connection "<name>"?` Deleting fails silently for non-Admins.

---

## 7. Step 3 — Create an application and bind connections

An **application** is your container for one migration project: its connection slots, its manifests (table lists) and its job history.

### 7.1 Create the application

1. Open **Applications**.
2. Click **New Application**.
3. In **Create New Application** enter **Application Name** (required, unique, e.g. `HR Migration`) and an optional **Description**.
4. Click **Create**.

If nothing happens, you probably lack Operator/Admin rights or the name already exists (the UI shows no error in either case).

The application appears as a card. Click **Manage** to open it. (The trash icon, *Delete Application*, is Admin-only and also deletes all its manifests and job history.)

### 7.2 Bind connection slots

The **Application Detail** page has two panels:

```
┌─ ← HR Migration ─────────────────────────────────────────────────┐
│ ┌─ Migration Manifests ─────────────┐ ┌─ Connection Slots ───────┐│
│ │ [Auto-Gen Manifest][Custom Builder]│ │ Oracle Test   [Assign]   ││
│ │                                    │ │ Oracle Live   [Assign]   ││
│ │ HR Schema Manifest v1.0            │ │ Postgres Test [Assign]   ││
│ │ Created …  [Edit]  [Run Job]       │ │ Postgres Live [Assign]   ││
│ └────────────────────────────────────┘ └──────────────────────────┘│
└───────────────────────────────────────────────────────────────────┘
```

The four slots let you keep test and production databases apart, and pick which pair to use every time you run a job:

| Slot | Holds |
|---|---|
| **Oracle Test** | A non-production Oracle source |
| **Oracle Live** | The production Oracle source |
| **Postgres Test** | A non-production PostgreSQL target |
| **Postgres Live** | The production PostgreSQL target |

1. On **Oracle Test**, click **Assign**. In **Assign Connection Slot** pick a profile from the list (only Oracle profiles are offered) and click **Assign**.
2. Do the same for **Postgres Test** (only PostgreSQL profiles are offered).
3. Repeat for the Live slots only when you are ready for production.

A bound slot shows the connection name and `host:port/service` with an **Unassign** button (`Unassign <slot> from this application?`). An empty slot shows **Not Bound**.

> **Tip — for your first run bind only *Oracle Test* and *Postgres Test*.**

---

## 8. Step 4 — Build the manifest (choose tables)

The **manifest** is the list of tables to migrate, with optional per-table row filters. O2P reads the table list and columns from Oracle ("discovery"), you review it, and the job copies exactly that list.

Choose one of two ways, both on the Application page.

### 8.1 Fast path — **Auto-Gen Manifest** (whole schema)

Best when you want every table of one Oracle owner.

1. Click **Auto-Gen Manifest**.
2. In **Auto-Generate Manifest**:
   - **Oracle Discovery Connection** — pick your Oracle profile.
   - **Oracle Schema / Owner** — e.g. `HR` (converted to upper case).
3. Click **Generate**.

O2P scans the schema and creates a manifest named **`<OWNER> Schema Manifest v1.0`** containing **all** discovered tables, with all columns.

Errors appear in a pop-up: `Oracle discovery failed: …`, `Invalid connection`, `No discovery cache found for connection and owner.`

> **Note.** The generated name is fixed. Running Auto-Gen a second time for the same owner in the same application will probably fail because the name already exists. Use **Custom Builder** instead, or delete the application and start fresh.

Then click **Edit** on the new manifest to review it (next section) — this is strongly recommended.

### 8.2 Custom path — **Custom Builder**

Best when you want only some tables, or want to filter rows.

1. Click **Custom Builder** (a new manifest) or **Edit** on an existing manifest.

```
┌─ ← Custom Manifest 2026-09-19 ───────── [Refresh Dictionary] [Save Manifest] ┐
│ Select and configure tables to migrate                                        │
│ Oracle Connection [Oracle HR Test ▾]     Schema / Owner [ HR ]                │
│                                                                               │
│ Add specific tables (dynamic - not limited to a full schema scan)             │
│ ┌───────────────────────────────────────────────┐                             │
│ │ EMPLOYEES, DEPARTMENTS                        │  [Add Tables]               │
│ └───────────────────────────────────────────────┘                             │
│ [ Search tables... ]                                                          │
│ ☑ Owner  Table Name     Est. Rows  Size (MB)  LOBs  Filter (WHERE)     Remove │
│ ☑ HR     EMPLOYEES        107,000       12      —    [ YEAR = 2024 ]     🗑    │
│ ☑ HR     DOCUMENTS   NO COLUMNS  9,000    800   LOB   [              ]   🗑    │
│ ☐ HR     AUDIT_LOG        5,200,000    900      —    [ (disabled)   ]    🗑    │
│                                                                               │
│ Selected: 2 tables · Total Rows: … · Total Size: … MB                         │
└───────────────────────────────────────────────────────────────────────────────┘
```

2. For a new manifest, edit the name at the top (default `Custom Manifest YYYY-MM-DD`). Manifest names must be unique within the application.
3. Pick **Oracle Connection** and type **Schema / Owner** (upper-cased automatically).
4. Get tables into the list — either or both:
   - **Refresh Dictionary** — scans the whole owner and merges every table into the list. Your ticks and filters are kept.
   - **Add specific tables** — type or paste names, one per line or comma-separated (`EMPLOYEES, DEPARTMENTS`), then click **Add Tables**. Only those tables are looked up. If none are found: `None of the requested tables were found in <OWNER>.`
5. Review the grid:

| Column | What to do |
|---|---|
| ☑ checkbox | Tick to migrate, untick to skip |
| **Owner**, **Table Name** | Read-only. An amber **NO COLUMNS** tag means O2P could not read the column list — see below |
| **Est. Rows**, **Size (MB)** | Estimates from Oracle statistics; use them to judge run time |
| **LOBs** | A red **LOB** tag means the table has CLOB/BLOB-type columns (slower, memory-heavy) |
| **Filter (WHERE)** | Optional row filter for this table (disabled while unticked) |
| Trash icon (**Remove from manifest**) | Delete the row from the list |

   Use **Search tables…** to filter the grid by table name or owner.

6. Click **Save Manifest**. It is disabled while the list is empty. There is **no "saved" message** for existing manifests — no red error bar means it worked. A new manifest switches to its own edit page after saving.

Possible pop-ups: `Select an Oracle connection and enter a schema/owner first.`, `Type or paste one or more table names first.`, `Add at least one table before saving.` Red-bar errors: `Failed to refresh discovery dictionary.`, `Failed to save manifest.`

### 8.3 Writing a row filter

Type **only the condition**, without the word `WHERE`:

```sql
CREATED_AT >= DATE '2026-01-01' AND STATUS = 'ACTIVE'
```

The filter is applied to the Oracle read **and** to the Oracle-side count used for validation. Not allowed: `;`, `--`, `/* */`, and the words `insert`, `update`, `delete`, `merge`, `drop`, `alter`, `create`, `execute`, `grant`, `revoke`. A rejected filter surfaces later as `The manifest WHERE clause contains unsupported SQL.`

> **Warning — filters and existing tables.** The PostgreSQL row count at validation covers the *whole* target table, and the table is truncated before loading. If you filter a table, the counts only match on an otherwise-empty target table.

### 8.4 Manifest review checklist

- [ ] No table shows **NO COLUMNS**. This tag means the account cannot read `ALL_TAB_COLUMNS` for that table. Untick or remove the table, or have the DBA fix access and click **Refresh Dictionary**.
- [ ] Tables you do **not** want (audit, temp, staging) are unticked.
- [ ] You know which tables have **LOB** columns and are large; expect them to be the slowest.
- [ ] The row estimate and size look plausible for the schema.
- [ ] Column names are plain identifiers (letters, digits, `_`, `$`, `#`, starting with a letter or `_`). A table with other names fails with `Unsafe Oracle identifier: …`.

### 8.5 How Oracle types become PostgreSQL types

There is **no UI to change column types or exclude columns**; O2P chooses automatically:

| Oracle | PostgreSQL |
|---|---|
| `VARCHAR2(n)`, `NVARCHAR2(n)` | `varchar(n)` (or `text` when there is no usable length) |
| `CHAR(n)`, `NCHAR(n)` | `char(n)` |
| `CLOB`, `NCLOB`, `LONG` | `text` |
| `NUMBER(p,0)` | `smallint` (p≤4), `integer` (p≤9), `bigint` (p≤18), else `numeric(p)` |
| `NUMBER(p,s)` / plain `NUMBER` | `numeric(p,s)` / `numeric` |
| `DATE` | `timestamp(0) without time zone` |
| `TIMESTAMP` | `timestamp without time zone` |
| `BLOB`, `BFILE`, `RAW`, `LONG RAW` | `bytea` |
| `BINARY_FLOAT` / `FLOAT`, `BINARY_DOUBLE` | `real` / `double precision` |
| `ROWID`, `UROWID` | `varchar(4000)` |
| `XMLTYPE` | `xml` |
| anything else | `text` |

> **Check these types manually.** `TIMESTAMP WITH TIME ZONE`, `TIMESTAMP WITH LOCAL TIME ZONE` and `INTERVAL` columns may **not** map as intended (time-zone information can be lost, and INTERVAL columns may fail to load). Also review any table with `XMLTYPE`, `BFILE` or very large LOBs on a small test table before the real run.

**Names are not converted.** Table and column names keep their Oracle upper-case spelling and are created quoted, so in PostgreSQL you must write `SELECT * FROM "HR"."EMPLOYEES"`. The target table always has the same name as the source table.

---

## 9. Step 5 — Launch the migration job

1. On the Application page, find your manifest and click the green **Run Job** button. **Launch Migration Job** opens.

```
┌─ Launch Migration Job ────────────────────────────────┐
│ Source Slot           [ Oracle Test        ▾ ]        │
│ Target Slot           [ Postgres Test      ▾ ]        │
│ Target Postgres Schema[ public               ]        │
│                                 [Cancel] [Launch Migration]
└───────────────────────────────────────────────────────┘
```

2. Choose:
   - **Source Slot** — `Oracle Test` or `Oracle Live`
   - **Target Slot** — `Postgres Test` or `Postgres Live`
   - **Target Postgres Schema** — default `public`; it **must already exist**
3. Click **Launch Migration**. (It is disabled if no Oracle or no PostgreSQL profile exists at all.)

### 9.1 What happens on launch

1. O2P creates the job (status **Draft**) and one table-run per ticked table.
2. **Preflight runs automatically** and must pass:

| Preflight check | Failure message |
|---|---|
| Oracle account can connect and read session privileges | `Oracle: Missing required SELECT ANY TABLE or session privileges.` (really means it could not connect/query) |
| PostgreSQL version is 14 or newer | `Postgres: Target version is older than 14. Unsupported.` |
| Account has `USAGE` and `CREATE` on the target schema (also fails if the schema does not exist) | `Postgres: Missing USAGE or CREATE privileges on schema '<schema>'.` |
| Account can create, insert into and drop a probe table | `Postgres: DDL Probe failed. Cannot create/insert/drop tables.` |

3. If everything passes, the job becomes **Queued** and the page opens **Job Details**. The Worker picks it up within a few seconds.

If preflight fails you see a red panel `Preflight check failed. Job launch aborted.` The specific check is **not** shown in the UI; use the list above (or ask your administrator to read the API log). Other launch errors: `Source or Target slot connections are not bound.` / `Target slot connection is not bound.` — bind the slot ([Step 3](#72-bind-connection-slots)).

> **Note.** Each failed launch leaves an unused **Draft** job in **Job Runs**. It is harmless; ignore it.

> **Preflight does not check** that the account can `SELECT` your specific Oracle tables, or `TRUNCATE`/`ALTER` existing PostgreSQL tables. Those problems appear during the run as failed tables.

### 9.2 Launching against Postgres Live — known limitation

Choosing **Postgres Live** shows an amber box: `Live target selected. Type RUN LIVE MIGRATION to continue.` **In the current version this does not work:** the server requires a different phrase, `MIGRATE <application-name> LIVE`, and the UI never sends it, so the launch is rejected with `Live target requires confirmation phrase: MIGRATE <App> LIVE`.

Until this is fixed, do Live runs like this: click **Launch Migration** once (this creates a Draft job and shows the error), then ask an administrator to call the API `POST /api/v1/jobs/{id}/launch` with `{"confirmationPhrase": "MIGRATE <application-name> LIVE"}` (Swagger UI at `/swagger` is available in Development). Always complete a full run on the **Test** slots first.

---

## 10. Step 6 — Monitor the job

### 10.1 Job Runs (the list)

**Job Runs — Monitor active and historical migration executions.** The list refreshes every **5 seconds**.

Each row: `Run #<id>`, `App: <name> • Target Schema: <schema>`, a status pill, and buttons:

| Button | Shown when | Effect |
|---|---|---|
| **View Details** | Always | Opens Job Details |
| **Cancel** | Running, Queued or Paused (Admin/Operator) | `Cancel job #N? Target tables are not dropped.` |
| **Retry** | Failed, Cancelled or CompletedWithErrors (Admin/Operator) | Re-queues failed work — read [Step 8](#12-step-8--fix-problems-and-re-run) first |

Admin also has **Cancel All & Restart Worker**: cancels every running/queued/paused job and restarts the Worker. Use only when the Worker seems stuck; target tables are not dropped.

Empty state: `No migration jobs have been executed yet. Go to your Application to launch one.`

### 10.2 Job Details (the live view)

Click **View Details**, or you land here after launching. The page refreshes every **2 seconds**.

```
┌─ ← Job Execution #12 ─ App: HR Migration • Schema: public ──── [Running] ┐
│ [Pause] [Cancel job]                                                      │
│ ┌ Rows Migrated ┐ ┌ Data Transferred ┐ ┌ Active Rate ┐ ┌ Active Workers ┐│
│ │   1,250,000   │ │      0 MB        │ │   38,000 R/s│ │    8 / 16      ││
│ └───────────────┘ └──────────────────┘ └─────────────┘ └────────────────┘│
│ Table-wise Migration Progress                                             │
│ EMPLOYEES  Target: EMPLOYEES   ████████████░░░ 80%   Loading      ▾       │
│   Chunk Heatmap (13 / 16 chunks done)   ■■■■■■■■■■■■■□□■                  │
│   Metrics: Rows Load … Data Load (KB) …                                   │
│   Post-Migration Verification: Count verification passed                  │
│   Oracle Src: 107000 | PG Target: 107000                                  │
└───────────────────────────────────────────────────────────────────────────┘
```

**Control buttons** (Admin/Operator only; a message box appears under the header after each click — the Worker acts within a few seconds):

| Button | Shown when | Effect |
|---|---|---|
| **Pause** | Running | `Pause requested — no new chunks will be claimed.` Chunks already copying finish |
| **Resume** | Paused | `Resume requested — job set back to Running.` |
| **Cancel job** | Running, Queued, Paused | Stops the job; **the Worker truncates the target tables** it was loading and restores constraints. In-flight chunks may still commit |
| **Retry failed** | Failed / Cancelled / CompletedWithErrors | Re-queues failed and cancelled work |

**Summary cards:** *Rows Migrated*, *Data Transferred* (currently always 0 — ignore), *Active Rate* (rows/s), *Active Workers* (the "/ 16" is a fixed label; the real limit is set by your administrator, default 8).

**Per table**, click the row to expand it:

- **Progress bar** — percentage of chunks done.
- **Chunk Heatmap** — one square per chunk. Blue = Running, green = Done, red = Failed or Cancelled, grey = waiting. Hover a red square to see the error text.
- **Metrics** — rows and data loaded, and the table's error message in red.
- **Post-Migration Verification** — appears after loading (see [Step 7](#11-step-7--verify-the-result)).

### 10.3 Status glossary

**Job status**

| Status | Meaning |
|---|---|
| Draft | Created but not launched (or launch was rejected) |
| Queued | Passed preflight; waiting for the Worker |
| Running | Worker is processing tables |
| Paused | You paused it |
| Completed | Every table finished and passed the row-count check |
| CompletedWithErrors | Finished, but at least one table failed or its count did not match |
| Failed | Job could not start (e.g. a bound connection is missing) |
| Cancelled | You cancelled it |

**Table status**

| Status | Meaning |
|---|---|
| Pending | Waiting its turn (tables are prepared one at a time) |
| Creating | Creating the table, or dropping constraints and truncating an existing one |
| Planning | Splitting the Oracle table into chunks |
| Loading (blue) | Copying rows |
| Validating (amber) | Restoring constraints and comparing row counts |
| Completed (green) | Loaded and counts match |
| CompletedWithErrors (orange) | Loaded, but counts do not match |
| Failed (red) | Preparation or a chunk failed |
| Cancelled (red) | Cancelled by a user |

**Chunk status:** Pending, Running, Done, Failed, Cancelled.

### 10.4 How long will it take?

There is no ETA. Use **Est. Rows** from the manifest and the *Active Rate* card to estimate. Throughput is capped at roughly 50,000 rows/second across the whole Worker, and tables are prepared one after another. LOB-heavy tables are much slower. You can close the browser; the job continues in the Worker.

---

## 11. Step 7 — Verify the result

### 11.1 Built-in check (row counts)

When a table finishes, expand it on **Job Details**:

- Green **Count verification passed** with `Oracle Src: N | PG Target: N` — counts are equal.
- Red **Count mismatch or validation failed** — counts differ, or the count query failed (the table status is *CompletedWithErrors*).
- `Validation checks pending data load completion.` — not finished yet.

A job is **Completed** only when every table passed.

> **What this check does not prove.** It compares row counts only — no checksums, no sampled values, no null counts. Equal counts do not guarantee identical data. Also, if the source table changes while the migration runs (there is no consistent snapshot), counts can differ legitimately: freeze or quiesce the source during the run.

### 11.2 Your own checks in PostgreSQL

Quote the upper-case names:

```sql
-- row count per table
SELECT COUNT(*) FROM "HR"."EMPLOYEES";      -- schema = the one you chose at launch

-- compare a numeric column total with Oracle
SELECT SUM("SALARY") FROM "HR"."EMPLOYEES";

-- spot-check a few rows against Oracle
SELECT * FROM "HR"."EMPLOYEES" ORDER BY "EMPLOYEE_ID" LIMIT 10;

-- list created columns and types
SELECT column_name, data_type, is_nullable
FROM information_schema.columns
WHERE table_schema = 'public' AND table_name = 'EMPLOYEES'
ORDER BY ordinal_position;
```

Suggested sign-off list:

- [ ] Job status is **Completed** and every table shows **Count verification passed**.
- [ ] Sums/min/max of key numeric and date columns match Oracle.
- [ ] Sampled rows match, including NULLs, empty strings (Oracle empty strings become NULL), and time-zone columns.
- [ ] LOB columns (text/bytea) spot-checked.
- [ ] Primary keys, indexes, foreign keys and sequences created by your DBA (see [section 15](#15-what-o2p-does-not-migrate)).

> The leftover table `_o2p_chunk_log` in the target schema is O2P's bookkeeping for duplicate-safe chunk loads. Keep it until you are done re-running; drop it afterwards if you like.

---

## 12. Step 8 — Fix problems and re-run

| Situation | What to do |
|---|---|
| **Job stuck in Queued** | Worker is not running or uses a different metadata database. Ask your administrator |
| **Preflight failed** | Fix the named cause (see [Step 5](#91-what-happens-on-launch)), then click **Run Job** again |
| **A table is Failed** | Expand it and read the red error text and the red chunk tooltips. Fix the cause, then **start a new job** (below) |
| **CompletedWithErrors, count mismatch** | Usually the source changed during the run, a target trigger/constraint interfered, or a filter was set. Fix, then re-run |
| **Chunk timed out** (`Chunk timed out after N minutes … (stall watchdog)`) | A slow read or network issue. Ask your administrator to lower concurrency or raise the timeout, then re-run |
| **Wrong tables/filters** | Edit the manifest, save, and launch a new job |
| **Want to stop** | **Cancel job**. The target tables being loaded are emptied |

### Re-run vs. Retry failed

- **Recommended: launch a new job** (same manifest, **Run Job** again). A new job starts each table from scratch: it truncates the target table and reloads everything, so the result is clean.
- **Retry failed** re-queues only the failed/cancelled chunks. But when a table fails, O2P empties the target table and restores constraints; chunks that had finished earlier are *not* copied again, so a retry can leave the table incomplete while still looking healthy. Use it only for a quick experiment, and always compare counts afterwards.

> **Re-running always overwrites.** Every new job truncates and reloads existing target tables. Never point a job at a target table holding data you want to keep.

---

## 13. Worked example: migrate the HR schema

Goal: copy Oracle schema `HR` to PostgreSQL schema `hr_target` in database `TARGET_DATABASE`, as an Operator.

**Prepare (DBA, once)**

1. In PostgreSQL create the schema and grant rights:
   ```sql
   CREATE SCHEMA IF NOT EXISTS hr_target AUTHORIZATION O2P_TARGET_USER;
   ```
2. In Oracle grant `CREATE SESSION` and `SELECT` on the `HR` tables to `O2P_SOURCE_USER`.

**In the UI**

1. Open the UI address. Sign in as `admin`, change the password when asked.
2. **Connections → New Connection**: name `Oracle HR Test`, engine `Oracle Database`, host `SOURCE_HOST`, port `1521`, service `SOURCE_SERVICE`, user `O2P_SOURCE_USER`, password → **Save Profile** → **Test**. Expect **Online (…ms)**.
3. **New Connection**: name `Postgres HR Target`, engine `PostgreSQL`, host `TARGET_HOST`, port `5432`, database `TARGET_DATABASE`, user `O2P_TARGET_USER`, password → **Save Profile** → **Test**. Expect **Online**.
4. **Applications → New Application**: name `HR Migration` → **Create** → **Manage**.
5. **Connection Slots**: **Assign** `Oracle Test` → `Oracle HR Test`; **Assign** `Postgres Test` → `Postgres HR Target`.
6. Click **Custom Builder**. Choose *Oracle HR Test*, type owner `HR`, click **Refresh Dictionary**.
7. Untick tables you do not need; check that none says **NO COLUMNS**; add a filter on a large table if you only want recent rows. Click **Save Manifest**. Go back to the application (← arrow).
8. On the manifest click **Run Job**. Source Slot **Oracle Test**, Target Slot **Postgres Test**, Target Postgres Schema `hr_target` → **Launch Migration**.
9. You arrive on **Job Details** with status **Queued**, then **Running**. Expand a table to watch the chunk heatmap turn green.
10. Wait for status **Completed**. Expand each table and confirm **Count verification passed**.
11. In PostgreSQL run the checks from [11.2](#112-your-own-checks-in-postgresql), then hand over to the DBA to add primary keys, indexes, foreign keys and sequences.

---

## 14. Troubleshooting by message

| Where | Message / symptom | Likely cause → fix |
|---|---|---|
| Login | `Invalid credentials` | Wrong password, or more than 5 attempts in 5 minutes → retype / wait |
| Login | `…temporarily locked…` | 5 failed attempts → wait 15 minutes or an Admin clicks **Unlock** |
| Login | `Cannot reach the API…` | API stopped or wrong address → call your administrator |
| Any page | Suddenly back at Sign in | 20-minute session expired → sign in again |
| Connections | **Failed** + `Connection failed: …` | Wrong host/port/service/credentials, firewall, or listener down; the host must be reachable from the *API/Worker* server, not your PC |
| Connections | `A connection profile named "…" already exists…` | Pick a different **Profile Name** |
| Connections | `…Admin role required` | Only Admin can create/edit/delete connections |
| Connections | `Could not decrypt stored credentials … Re-enter the connection's password` | Encryption keys changed → **Edit** the profile and enter the password again |
| Applications | **Create**/trash does nothing | Missing role, or duplicate name (no visible error) |
| Auto-Gen | `Oracle discovery failed: …` | Bad Oracle profile or account cannot read the dictionary → **Test** the connection; check grants |
| Auto-Gen | `No discovery cache found for connection and owner.` | Discovery returned nothing (wrong owner name?) → check spelling; run **Refresh Dictionary** in Custom Builder |
| Builder | **NO COLUMNS** tag | Account cannot read the table's columns → DBA grants access, then **Refresh Dictionary** |
| Builder | `None of the requested tables were found in <OWNER>.` | Table does not exist, wrong owner, or no visibility. Names are upper-cased, so mixed-case Oracle names cannot be targeted |
| Launch | `Source or Target slot connections are not bound.` | Assign the slot on the Application page |
| Launch | `Preflight check failed. Job launch aborted.` | See the four checks in [9.1](#91-what-happens-on-launch); most often the target schema does not exist or the account lacks `USAGE, CREATE` |
| Launch | `Live target requires confirmation phrase…` | Known limitation, see [9.2](#92-launching-against-postgres-live--known-limitation) |
| Job | Stays **Queued** | Worker not running → administrator starts it |
| Job | Table **Failed**, `Unsafe Oracle identifier: …` | Column/table name outside letters, digits, `_ $ #` → exclude the table |
| Job | `The manifest WHERE clause contains unsupported SQL.` | Filter has `;`, comments or a forbidden keyword → edit and simplify |
| Job | Chunk error `22P03 incorrect binary data format`, or a type mismatch | Existing target table has different column types than the source → drop the target table and let O2P create it, or fix the column types |
| Job | NOT NULL violation on a column | Oracle treats `''` as NULL; O2P loads empty strings as NULL, which fails on a target NOT NULL column → clean the data or relax the constraint |
| Job | `Constraint restore failed after load: …` | Loaded data violates a restored constraint, or missing rights → fix the data/constraint and re-run |
| Job | `Chunk timed out after N minutes…` | Slow read/network or heavy LOBs → administrator lowers concurrency or raises timeout; re-run |
| Job | ORA-50000 / connection timeouts to Oracle | Too many concurrent Oracle sessions → administrator lowers concurrency |
| Job | **CompletedWithErrors**, counts differ | Source changed during the run, target trigger, or a filter → freeze source, re-run |
| PostgreSQL | `relation "employees" does not exist` | Names are upper-case and quoted → use `"EMPLOYEES"` |

Still stuck? Collect: the job number, the failing table and its red error text, and the time. Your administrator can find the details in the API and Worker logs (`logs/`), or with the `tools/job-inspect` command-line tool.

---

## 15. What O2P does not migrate

O2P copies **table structures and rows**. Plan a DBA task for the rest.

| Not migrated | What to do |
|---|---|
| Primary keys, unique keys, foreign keys, check constraints on **newly created** tables | Create them in PostgreSQL after the load (constraints already on a pre-existing target table are restored) |
| Indexes | Create after the load (faster than loading into indexed tables) |
| Sequences, identity columns, column defaults | Recreate; set sequence values above the current max IDs |
| Views, materialized views | Recreate/convert |
| Procedures, functions, packages, triggers | Rewrite in PL/pgSQL |
| Synonyms, grants, users, roles | Recreate |
| Ongoing changes (CDC / replication) | Not supported — freeze the source, or plan a cut-over window |
| Reject rows | A bad row fails its whole chunk; there is no reject table |

Also remember: table and column names keep their upper-case spelling and are created in quotes, so application SQL against the new database must quote identifiers, or you must rename objects to lower case afterwards (`ALTER TABLE "HR"."EMPLOYEES" RENAME TO employees;`).

**Post-migration checklist for the DBA**

1. Create PKs, unique constraints, FKs, indexes.
2. Recreate sequences/identities and reset their values.
3. Recreate views, functions and triggers.
4. Run `ANALYZE` on the migrated schema.
5. Grant application roles.
6. Compare data more deeply (sums, samples, checksums).
7. Remove the `_o2p_chunk_log` table when no more re-runs are needed.

---

## 16. FAQ and glossary

**Can I migrate my whole Oracle database in one go?** No. You migrate one owner (schema) at a time per manifest; you can create several manifests in an application.

**Can I copy only some columns?** Not from the UI. Whole tables only (with optional row filter).

**Can I change a PostgreSQL column type?** Not from the UI. Create the target table yourself first with the types you want; O2P keeps an existing table as it is and loads into it (column names, order and compatible types must match).

**Does the Settings page speed things up?** No, it only stores values in your browser.

**Can two people run jobs at the same time?** Jobs are prepared one at a time by a single Worker; extra jobs wait their turn.

**Is it safe to close the browser?** Yes. The Worker keeps running the job. Reopen **Job Runs** later.

**Can I pause without losing progress?** Yes, **Pause** stops claiming new chunks; **Resume** continues. Finished chunks are kept.

**Where is my password stored?** Connection passwords are encrypted in the O2P metadata database and never displayed again.

| Term | Meaning |
|---|---|
| **Application** | A project container: slots, manifests, jobs |
| **Slot** | One of Oracle Test/Live, Postgres Test/Live |
| **Manifest** | The list of tables (and filters) to migrate |
| **Discovery / Dictionary** | Reading table and column definitions from Oracle |
| **Preflight** | Automatic safety checks when a job is launched |
| **Job (Run)** | One execution of a manifest against a chosen source/target |
| **Chunk** | One slice of a table (up to 16 per table) copied by one worker |
| **Worker** | Background service that actually copies data |
| **Owner / Schema** | Oracle user that owns the tables |

**More documentation:** [User-Guide.md](User-Guide.md) (setup, permissions, tuning, production notes) · [DBA_Grants.md](DBA_Grants.md) · [Ops_Runbook.md](Ops_Runbook.md) · [O2P_Production_Hardening_Plan.md](O2P_Production_Hardening_Plan.md)

# O2P operational tools

Small, standalone console utilities for operating/debugging a deployed O2P instance. Each reuses the
app's own `AddMetadataInfrastructure` DI setup, so it talks to the metadata database exactly like the
API/Worker do (same EF model, same Identity config). They are **not** part of `src/O2P.slnx` and are
run on demand with `dotnet run`.

All tools read the metadata DB connection from the `ConnectionStrings__MetadataDb` env var (the same
one the API/Worker use), or from `O2P_METADATA_CONN` as a fallback. Example connection string:

```
Host=application.naasbd.com;Port=7936;Database=nonoraclemigrationdb;Username=<user>;Password=<pw>
```

(The `.env` at the repo root holds `O2P_METADATA_DB/USER/PASSWORD`; the deployed connection host is
`application.naasbd.com:7936` — see `scripts/Initialize-O2PEnvironment.ps1`.)

## admin-password-reset
Force-resets an O2P user's password (default `admin`) **without** the current password, and clears any
lockout — use it to recover a locked-out or forgotten admin account. Sets `MustChangePassword`, so the
new password is temporary and must be changed on next login.

```powershell
$env:ConnectionStrings__MetadataDb = "Host=application.naasbd.com;Port=7936;Database=nonoraclemigrationdb;Username=<user>;Password=<pw>"
$env:O2P_RESET_NEWPW = "<temp password, >=12 chars, upper+lower+digit+symbol>"
# $env:O2P_RESET_USER = "admin"   # optional, defaults to admin
dotnet run --project tools/admin-password-reset -c Release
```

Password policy (from `DependencyInjection.cs`): length >= 12, upper, lower, digit, non-alphanumeric,
>= 4 unique characters.

## job-inspect
Read-only dump of the latest job run (or a specific `job#` passed as an argument): table statuses,
chunk counts by status, chunking strategies used, per-chunk error messages, and validation results.
Handy when there is no `psql` on the box.

```powershell
$env:ConnectionStrings__MetadataDb = "..."
dotnet run --project tools/job-inspect -c Release            # latest job
dotnet run --project tools/job-inspect -c Release -- 11      # a specific job id
```

## job-cancel
Cancels one or more job runs (sets the job + its non-terminal tables/chunks to `Cancelled`) so a stuck
"Running" job is cleared and the worker stops picking it up. **Only metadata is changed — target
tables are not dropped.**

```powershell
$env:ConnectionStrings__MetadataDb = "..."
$env:O2P_CANCEL_JOBS = "10,11"
dotnet run --project tools/job-cancel -c Release
# or: dotnet run --project tools/job-cancel -c Release -- 10 11
```

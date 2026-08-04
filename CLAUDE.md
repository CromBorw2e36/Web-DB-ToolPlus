# CLAUDE.md — ToolPlus

## HARD RULE: never touch the connected databases yourself

Every module points at somebody's live production database. Rules:

1. **Do not connect to a module database from your own tooling** — no `sqlcmd`, no
   `dotnet ef dbcontext scaffold`, no EF query from a script, **not even a SELECT**. If you
   need to know what the real data looks like, ask, or hand over a script for the maintainer to
   run themselves.
2. **Do not change the structure of a module database** — no CREATE/ALTER/DROP of tables,
   views, sequences or indexes, no added columns. A business database must stay completely
   clean of this tool's footprint.
3. **Never add EF entities for a module database.** The data layer only ever needs a
   `DbConnection`; structure is read from `sys.objects` / `sys.columns` at run time. That is
   deliberate — it is why ToolPlus works against any table without scaffolding, and scaffolding
   would require connecting to the database anyway.
4. **Everything ToolPlus generates goes in its own SQLite file**, `ToolPlus/App_Data/toolplus.db`
   (`ToolDbContext`). Blueprints, audit log, relation hints, settings, presets — all of it
   lives there.
5. **When running the app to test, only hit routes that need no login** (the login page, static
   files). Do not log in, do not ask for a password. Anything that touches a module database is
   for the user to click.

The app is of course allowed to `UPDATE` module data when **the user** hits save in the UI —
that is the entire point of the tool. But the statement must be user-initiated, after they have
seen the before/after diff, and it always writes an audit row. `ToolPlus:AllowEdit = false`
disables every write path.

## What this application is

An internal web tool for the data-fixing chores that would otherwise be done by handing out
SSMS access. See `README.md` for how to run it, how login works, and what the features are.

## Architecture

```
Controller ──> Service ──┬──> IModuleContext     (SQL Server business data — READ + user-initiated UPDATE only)
                         ├──> ToolDbContext      (SQLite: accounts, sessions, blueprints, audit)
                         └──> sys.objects / sys.columns  (metadata, cached 10 minutes)
```

**One connection string is one module.** `ModuleRegistry` builds the list at startup and
`IModuleContext` holds the connection of the module being viewed for the whole request. No
connection string is mandatory or special.

The module comes from the first URL segment (`/billing/explorer/index`, via the `:module` route
constraint). Two rules you must follow when adding code:

- Links in views go through `Module.Url("/...")`, never an absolute path — hard-coding one
  means a single click silently drops the user back to the default module.
  In JS, `request()` adds the prefix for you.
- Anything stored in SQLite that references a table or column of a business database needs a
  `ModuleKey` column and must be filtered by it (`Blueprint` and `RelationHint` already are).
  `AuditLog` uses a `Module` column, which `AuditService` fills in so call sites do not have to
  — except for account-level events (login, user edits), which leave it empty; a new event of
  that kind must be declared in `AuditService.AccountLevel`.

**Identity lives entirely in SQLite** and is not shared with the applications that own the
data: `ToolUser` (BCrypt), `ToolSession` (random token, only its SHA256 is stored),
`ToolUserModule` (per-module read/write permission). The consequence to remember: **if an
account is disabled in the owning application, ToolPlus does not find out** — use `ToolUser`
`ExpiresAt` to force a periodic review.

- `BaseController` — `memoryCache`, `currentUser` (`ToolUser`), `sessionToken`, `Success()` / `Error()` helpers
- `TokenCheckAttribute` — authenticates the SQLite session, then authorises against module
  permissions; flags `AdminOnly`, `SkipModuleCheck` (page belongs to the account, not to a
  module), `SkipPasswordChange`
- `UserService` — login, sessions, module permissions, account management (`/user`, admin only)
- `MetadataService` — reads database structure; also the **whitelist layer**: every table and
  column name received from a client must be validated through it before it goes into SQL
- `ExplorerService` — search, single-row edit by primary key, free-form SELECT
- `MenuDefinition` — the whole menu declared in one place; a new page is one more line

## Conventions

- Every query against a module database runs
  `SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED`, so the tool never holds locks that could
  stall a production database.
- Editing data: the table must have a primary key, exactly one row per statement, and an
  `UPDATE` affecting any other number of rows is rolled back. Views, primary-key columns and
  identity/computed columns are never editable.
- The frontend uses no third-party libraries (no Bootstrap, no jQuery):
  `wwwroot/css/toolplus.css` + `wwwroot/js/site.js` + `wwwroot/js/pages/*.js`. Design system in
  `design-system/toolplus/MASTER.md`.
- Code, comments and documentation are in English; comments explain *why*, they do not restate
  the code. User-facing UI strings are in Vietnamese — keep new UI text consistent with that.
- The project is MIT licensed and public: no company names, internal hostnames, IP addresses,
  real account names or production connection strings anywhere in the repository.

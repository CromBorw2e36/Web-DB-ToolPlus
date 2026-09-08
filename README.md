# ToolPlus

An internal web tool for looking up and fixing data directly in SQL Server databases —
a safer, auditable replacement for handing out SSMS access.

Point it at one or more connection strings and you get, per database: search across any
table, in-place single-row edits with a before/after diff, a read-only SELECT console,
saved multi-table "blueprints" for recurring lookups, and a full audit log of everything.

> The user interface is in Vietnamese. Code, configuration and documentation are in English.

## Running

```bash
cp ToolPlus/appsettings.local.example.json ToolPlus/appsettings.local.json   # then fill in real values
dotnet run --project ToolPlus/ToolPlus.csproj                                # http://localhost:5300
```

**Connection strings and the bootstrap password live in `appsettings.local.json`** — that
file is gitignored so secrets never reach the repository. It is loaded last, so it overrides
every other appsettings file.

### Docker

```bash
cp ToolPlus/appsettings.local.example.json ToolPlus/appsettings.local.json   # then fill in real values
docker compose up --build                                                    # http://localhost:8300
```

`appsettings.local.json` and the `App_Data` SQLite store are mounted at runtime (see
`docker-compose.yml`), never baked into the image. The container listens on `8300`; override
the host-side port with a `.env` file (`TOOLPLUS_PORT=8081`) instead of editing the compose file.

## Modules — one connection string is one module

Add a line to `ConnectionStrings`, restart, and you have a new module: search, blueprints and
audit all work against that database **with no code changes**. Optional name/icon/edit-lock
go under `ToolPlus:Modules:<key>`.

**The module lives in the first URL segment** — `/billing/explorer/index` — rather than a
dropdown backed by a cookie: this tool can change real data, so "which database am I touching"
must be visible in the address bar, and a link you send someone else opens the same place you
were looking at. The topbar has a module switcher (only modules you may access); switching
rewrites the URL and keeps the current page.

A URL with no module falls back to `ToolPlus:DefaultModule`. If the signed-in user has no
access to the default module they are **redirected** to the first module they can access rather
than blocked — only a URL naming a module they were never granted returns a permission error.

No connection string is mandatory. Every module must be **SQL Server**, because table
structure is read from `sys.objects`. A connection string name must not collide with one of
the tool's own page names (`home`, `login`, `explorer`, `blueprint`, `user`) — the app refuses
to start if it does.

**Blueprints, relation hints and audit rows are all module-scoped.** A blueprint drawn against
one database does not show up in another, and the audit log records which database was edited.

## Signing in

**ToolPlus has its own accounts — it does not share an identity store with the applications
that own these databases.** Everything lives in SQLite:

| Table | Purpose |
|---|---|
| `ToolUser` | Account, BCrypt password hash, admin flag, `ExpiresAt`, `MustChangePassword` |
| `ToolSession` | Login session. The cookie holds a random 32-byte token; the DB stores only its SHA256 |
| `ToolUserModule` | Per-account read / write permission on each module |

On first run, if `ToolUser` is empty the app creates an administrator from `ToolPlus:Bootstrap`
(username + password in `appsettings.local.json`). Once you are in, **delete that password** —
the app keeps warning in the log until you do — and manage accounts at `/user`.

Access is checked in three layers, all handled by `TokenCheckAttribute`: valid session →
(where required) must be admin → must have permission on the module being opened. Admins have
full access to every module. **Write** permission is the AND of three flags:
`ToolPlus:AllowEdit`, `Modules:<key>:AllowEdit`, and `ToolUserModule.CanEdit`.

> **Trade-off to keep in mind:** because identity is separate, **when someone leaves and their
> account is disabled in your main application, ToolPlus does not find out**. Set `ExpiresAt`
> on non-admin accounts so they have to be reviewed periodically instead of living forever.

The session cookie is named `tp_token` (`ToolPlus:CookieName`). Keep it different from the
cookie of any other app you run on the same host: cookies are not scoped by port, so a shared
name means signing in here logs you out there.

## Features

| Page | Purpose |
|---|---|
| Blueprints `/blueprint` | Drop tables on a canvas, link field to field, save and reuse |
| Explorer `/explorer` | Search by keyword or condition, edit in place, SELECT console, history |
| Field sync `/sync` | Repair one column from another module's database, row by row, after a diff |
| Users `/user` | (Admin) ToolPlus accounts and their permission on each module |

### Own store (SQLite)

Everything the tool generates lives in `ToolPlus/App_Data/toolplus.db` — **no extra tables are
created in the databases you connect to**. It holds `ToolUser` / `ToolSession` /
`ToolUserModule` (identity, see above), `Blueprint`, `AuditLog` (edits / logins / SELECT
statements / blueprint activity / field syncs) and `RelationHint` (table relationships the user drew by hand,
reused as suggestions later).

The schema is created by `EnsureCreated()` at startup; there are no migrations.
`EnsureCreated()` skips a file that already exists, so the three identity tables are also
created by `ToolDbContext.EnsureIdentityTables()` (`CREATE TABLE IF NOT EXISTS`) — that way an
installation from before those tables existed does not have to delete the file and lose every
saved blueprint. For the same reason, columns added later are applied by `EnsureModuleColumns()`
via `ALTER TABLE` (checking `pragma_table_info` first, since SQLite has no
`ADD COLUMN IF NOT EXISTS`), and blueprints / hints from before modules existed are assigned to
`DefaultModule`.

### Audit log (`AuditLog`)

Written to SQLite, never to a file. Types: `update`, `login`, `login-fail`, `password-change`,
`user-save`, `user-delete`, `query`, `blueprint-save`, `blueprint-delete`, `blueprint-run`,
`sync` —
each with user, IP, module, table, key, a readable **label** for the row, before/after values and
the SQL statement.

The label exists because a primary key in these databases is usually a GUID, and a log of GUIDs is
a log nobody can read. `MetadataService.GuessLabelColumn` picks the most identifying short text
column of the table (username, code, name, …) and its value is captured **at write time** — never
looked up afterwards, since by then the row may be gone or that value changed. The field-sync page
shows which column it settled on and lets you override it. Viewable on
the **History** tab of `/explorer` (which reads the whole log, not just Explorer's own rows).

Expanding an entry offers **copy buttons that produce runnable SQL**. This matters because the
logged statement is parameterised — `SET [c] = @v WHERE [k] = @k0`, since values never reach the
SQL text — so pasting it into SSMS would only complain about undeclared variables. The values are
recorded separately in `KeyJson` / `ChangesJson`, and the buttons rebuild a real statement from
all three: a `SELECT` for the row, the `UPDATE` as it ran, and the **reverse** `UPDATE` that undoes
it. Anything that writes comes wrapped in `BEGIN TRAN … ROLLBACK`, so it can be run, counted, and
only then changed to `COMMIT`.

Rows written by one field sync share a `RunId`. A summary entry says how many rows a run wrote but
not which, and they are scattered through the list, so it carries **"xem các dòng đã sửa"** —
expanding them in place, each with its own copy buttons. The same grouping is what makes
**"copy hoàn tác cả lần chạy"** possible — one transaction reversing every row that run touched, however many there were. Each
reversing statement carries `AND [col] = <value the log says was written>`, so a row somebody has
edited since is simply not matched.

Column types are not recorded, so literals go out as strings and let SQL Server convert
(`N'123'` into an `int` column is fine); `bit` is mapped to `1`/`0` explicitly because `N'True'`
would not convert.

### Blueprints

A canvas holds one **PARAMETER** node (where input enters) plus table nodes. Drag from the
**right-hand** dot of a column (supplies a value) to the **left-hand** dot of another column
(gets filtered) to link them.

Constraint: each table accepts **exactly one inbound link** — a blueprint must be a tree. That
is what makes cascading possible at run time (parent yields values → child filters by them)
while every row keeps its own table's primary key and stays safely editable. Collapsing this
into one big JOIN would lose both properties.

When you add a table, the tool suggests links in order of confidence: relationships you have
linked before → real foreign keys in `sys.foreign_keys` → inference by name.

Name inference currently assumes the `Ma<Entity>` key convention ("ma" is Vietnamese for
code/id): `MaCustomer` is taken to reference the primary key of a table whose name ends in
`Customer`, e.g. `sales_Customer.Ma`. A schema that names its keys differently simply gets no
suggestions from that branch — link the columns by hand once and the learned-relationship branch
covers it from then on.

### Running a blueprint

`/blueprint/chay/{id}` — fill in the parameters and hit Run. The engine issues **one query per
level**: the root node filters by the parameters, child nodes filter with
`IN (values from the parent node)`, batched 200 keys at a time (SQL Server caps a statement at
2100 parameters).

- Click a row in a parent table to **filter the child tables** down to that row; click again to
  clear.
- The ✎ button on a row opens edit → before/after diff → save. Only columns ticked in the
  blueprint are editable.
- Nullable columns get their own `NULL` checkbox, so there is no sentinel value to memorise.
- A broken node (dropped table, removed column) reports its own error while the other nodes
  still return data.

A **month** parameter linked to a date column becomes a
`>= start of month AND < start of next month` range instead of an equality test. Condition
values accept `{parameterName}`, and month parameters additionally support
`{parameterName:tu}` / `{parameterName:den}` (from / to).

Explorer reads structure from `sys.objects` / `sys.columns`, so it **works against any table
with no scaffolded entity**. After adding a table to the database, click reload-structure.

### Field sync (`/<module>/sync`)

For the case where a column in the live database was wiped or corrupted and a second database —
last month's copy, a standby, a partner system — still holds the good values.

Pick the source module and table, the **match column** that pairs a row here with a row there
(a business key such as `MaHopDong`, not necessarily the primary key), and the column to repair.
Optionally name a **label column** — what makes a line recognisable as belonging to a particular
person or document; left blank, the server picks one and tells you which.
The tool reads both sides and shows a diff, one line per row:

| State | Meaning |
|---|---|
| `Sẽ điền` | Target is empty, source has a value — ticked by default |
| `Sẽ ghi đè` | Both sides hold a value and they differ — never ticked by default |
| `Không ghi được` | The source value does not fit the target column (too long, wrong type) |
| `Nguồn trùng khóa` | Several source rows share the match value but disagree — never guessed |
| `Không thấy ở nguồn` / `Nguồn cũng trống` / `Giống nhau` | Nothing to do |

Tick what should be written, and the rows are updated one statement at a time.

Three modes decide what a run may do: **fill empty cells only** (the default — nothing holding
data is touched), **fill and overwrite**, or **compare only**, which refuses to write at all.
The mode is enforced on the server, not just in the UI.

How it is kept safe:

- **The target is always the module in the URL.** The other database is only ever read, which is
  what makes the edit permission and the module stamped on each audit row correct by
  construction. To repair a different database, switch module in the top bar.
- Permission on the *source* module is checked explicitly — `TokenCheckAttribute` only authorises
  the module in the URL, so an account granted one database cannot read another by naming it in
  the request.
- **Apply never trusts the browser.** It re-sends the same configuration, the server rebuilds the
  comparison from scratch, and the request carries only the primary keys that were ticked — never
  the values to write.
- Each `UPDATE` carries the value the preview showed (`... AND [col] = @old`). If someone changed
  the row in between, the statement matches nothing and the row is reported as skipped rather
  than silently overwritten.
- One transaction per batch (`Sync:BatchSize`), one statement per row; a statement matching more
  than one row rolls its batch back and stops the run.
- Every written row gets its own `AuditLog` entry with the before/after values, plus one `sync`
  summary row.

The two databases are usually on different servers, so there is no cross-database JOIN and no
temp table: the candidate rows are read from the target, their match values are looked up in the
source 200 at a time, and the comparison happens in memory. A run is capped at `Sync:MaxRows`
rows and says so when it hits the cap.

### Editing data

Double-click a cell → edit → `Enter` (move down) / `Tab` (move right) / `Esc` (cancel).
Edited cells turn yellow and the bottom bar offers "Review & save" → before/after diff →
confirm.

Special input values:

| Input | Result |
|---|---|
| empty | `NULL` if the column is nullable, otherwise an empty string |
| `{null}` | always `NULL` |
| `{empty}` | always an empty string |

### Safety constraints

- Table and column names coming from the client are always checked against `sys.columns`
  before being interpolated into SQL; values always travel as parameters.
- Only tables **with a primary key** can be edited, **one row at a time** — an `UPDATE` that
  hits any number of rows other than one is rolled back.
- Views, primary-key columns and identity/computed columns cannot be edited.
- The console accepts only `SELECT` / `WITH`, and rejects write keywords (including
  `SELECT … INTO`) and `;`.
- Every statement runs at `READ UNCOMMITTED` so the tool never holds locks on a production
  database.
- Every edit is written to `AuditLog` in SQLite (user, IP, key, before/after values, SQL).

## Configuration (`appsettings.json`, `ToolPlus` section)

| Key | Meaning |
|---|---|
| `Bootstrap` | Administrator created on first run (`Username` / `Password` / `FullName`). Only effective while `ToolUser` is empty |
| `Modules` | Per-module extras: `Name`, `Icon`, `AllowEdit`, `Enabled` |
| `DefaultModule` | Module opened when the URL does not name one. Empty = first connection string |
| `SessionDays` | How many days a session lives before requiring a fresh login |
| `AllowEdit` | `false` = the whole instance is read-only |
| `MaxRows` | Cap on rows returned per request |
| `Sync:MaxRows` | Rows compared in one field-sync run (default 5000) — separate from `MaxRows`, which caps what a grid shows |
| `Sync:BatchSize` | Rows written per transaction during a field sync (default 100) |
| `SearchCollation` | Collation used for accent-insensitive search; `""` keeps accents significant |
| `QueryTimeoutSeconds` | Statement timeout |
| `CookieName` | Session cookie; keep it distinct from other apps on the same host |
| `HostApp` | Optional convenience link to the application that owns the data (`Name`, `Url`) |

## Architecture

```
Controller ──> Service ──┬──> IModuleContext  (SQL Server of the module being viewed)
                         ├──> ToolDbContext   (SQLite: accounts, sessions, blueprints, audit)
                         └──> sys.objects / sys.columns  (metadata, cached 10 minutes)
```

- `ModuleRegistry` / `IModuleContext` — one connection string is one module; `CacheKey()`
  prefixes everything read from a database with the module key (two databases can both have
  `dbo.sys_User`, and a shared cache would serve one for the other)
- `BaseController` — `memoryCache`, `currentUser`, `sessionToken`, `Success()` / `Error()` helpers
- `TokenCheckAttribute` — authenticates the session, then authorises against module
  permissions; flags `AdminOnly`, `SkipModuleCheck`, `SkipPasswordChange`
- `UserService` — login, sessions, module permissions, account management
- `MetadataService` — reads database structure and doubles as the table/column whitelist
- `ExplorerService` — search, single-row edit, SELECT console
- `SyncService` — compares one column across two modules and writes the rows the user picked;
  reaches the second database through `IModuleScopeFactory`, which is the only way to get a
  connection to a module other than the one in the URL
- `AuditService` — writes and reads the log in SQLite

The frontend uses no third-party libraries: `wwwroot/css/toolplus.css` +
`wwwroot/js/site.js` + `wwwroot/js/pages/*.js`. The design system is documented in
`design-system/toolplus/MASTER.md`.

## Known gaps

- The history tab does not show the `Module` column yet, even though the data is recorded.
- The log has no filters: `IAuditService.ReadLatestAsync` accepts `type` / `user` but
  `/explorer/log` does not pass them, and there is no module filter.
- The log still lives in a tab of Explorer; there is no dedicated page.
- Name-based link inference only understands the `Ma<Entity>` convention (see Blueprints);
  `<Entity>Id` and other conventions are not recognised yet.
- Field sync compares values through the same string form the grid uses, so a column with finer
  precision than that string keeps (`datetime2(7)`, for instance) can report an overwrite as
  "changed since the preview" and skip it. Filling empty cells is unaffected.
- Field sync handles one column per run, and has no saved presets — the same repair next month
  has to be configured again.
- Undo is a script to copy into SSMS, not a button: ToolPlus does not run the reverse itself.
- The UI is Vietnamese only — no localisation layer.

## License

MIT — see [LICENSE](LICENSE).

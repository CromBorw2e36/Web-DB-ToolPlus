using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using ToolPlus.Common;
using ToolPlus.Models.Explorer;
using ToolPlus.Models.Sync;
using ToolPlus.Models.ToolEntity;
using ToolPlus.Services.Explorer;
using ToolPlus.Services.Interface;

namespace ToolPlus.Services.Sync
{
    /// <summary>
    /// Repairs one column of a table from the same column in ANOTHER database.
    ///
    /// The two databases are usually on different servers, so there is no cross-database JOIN to
    /// lean on and no temp table to stage into (a business database must stay clean of this tool's
    /// footprint). Instead: read the rows that need fixing from the target, look their match values
    /// up in the source in batches, and compare in memory.
    ///
    /// The direction is fixed and not configurable: the target is always the module in the URL. That
    /// is what makes the edit permission, the read-only flags and the module stamped on every audit
    /// row come out right without any of them having to know this feature exists.
    /// </summary>
    public class SyncService : ISyncService
    {
        /// <summary>Both sides read dirty — this tool must never hold a lock on a production database.</summary>
        private const string NoLock = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;\n";

        /// <summary>SQL Server caps a statement at 2100 parameters; batch the IN list well below that.</summary>
        private const int KeyChunk = 200;

        /// <summary>Types SQL Server refuses to put next to "=" — they have to be cast first.</summary>
        private static readonly HashSet<string> LegacyText = new(StringComparer.OrdinalIgnoreCase)
            { "text", "ntext", "xml" };

        private readonly IModuleContext _module;
        private readonly IMetadataService _meta;
        private readonly IModuleScopeFactory _scopes;
        private readonly MemoryCacheManager _cache;
        private readonly IAuditService _audit;
        private readonly ToolPlusOptions _opt;

        public SyncService(IModuleContext module, IMetadataService meta, IModuleScopeFactory scopes,
            MemoryCacheManager cache, IAuditService audit, IOptions<ToolPlusOptions> opt)
        {
            _module = module;
            _meta = meta;
            _scopes = scopes;
            _cache = cache;
            _audit = audit;
            _opt = opt.Value;
        }

        // --------------------------------------------------------------- schema

        public async Task<List<TableInfo>> TablesAsync(ModuleDefinition? module)
        {
            if (module == null || module.Key == _module.Current.Key) return await _meta.GetTablesAsync();

            await using var scope = _scopes.Open(module.Key);
            return await MetaFor(scope).GetTablesAsync();
        }

        public async Task<List<ColumnInfo>> ColumnsAsync(ModuleDefinition? module, string table)
        {
            if (module == null || module.Key == _module.Current.Key)
            {
                var own = await _meta.ResolveTableAsync(table)
                          ?? throw new InvalidOperationException($"Table \"{table}\" not found");
                return await _meta.GetColumnsAsync(own);
            }

            await using var scope = _scopes.Open(module.Key);
            var meta = MetaFor(scope);
            var info = await meta.ResolveTableAsync(table)
                       ?? throw new InvalidOperationException(
                           $"Table \"{table}\" not found in module \"{module.Name}\"");

            return await meta.GetColumnsAsync(info);
        }

        /// <summary>
        /// The whitelist layer, pointed at the second database. Built by hand rather than injected:
        /// the container's MetadataService is bound to the request's own module, and this one has to
        /// read a different connection inside the same request.
        /// </summary>
        private IMetadataService MetaFor(IModuleScope scope) => new MetadataService(scope, _cache);

        // ----------------------------------------------------------------- plan

        public async Task<SyncPlanResult> PlanAsync(SyncPlanRequest req, ModuleDefinition source)
        {
            var watch = Stopwatch.StartNew();
            await using var scope = _scopes.Open(source.Key);

            var prep = await PrepareAsync(req, source, scope);
            prep.Result.ElapsedMs = watch.ElapsedMilliseconds;
            return prep.Result;
        }

        /// <summary>Everything Plan and Apply have in common: resolve both sides, read them, classify.</summary>
        private async Task<Prepared> PrepareAsync(SyncPlanRequest req, ModuleDefinition source, IModuleScope scope)
        {
            var p = new Prepared { Result = new SyncPlanResult(), SourceConnection = scope.Connection };
            var warn = p.Result.Warnings;

            // ---- target: the module in the URL, the only side that is ever written
            p.Table = await _meta.ResolveTableAsync(req.TargetTable)
                      ?? throw new InvalidOperationException($"Table \"{req.TargetTable}\" not found");

            if (p.Table.IsView)
            {
                throw new InvalidOperationException($"{p.Table.Full} is a view — nothing can be written to it");
            }

            p.Columns = await _meta.GetColumnsAsync(p.Table);
            p.Keys = p.Columns.Where(c => c.IsPrimaryKey).ToList();
            if (p.Keys.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Table {p.Table.Full} has no primary key — the tool cannot edit it safely");
            }

            p.Column = _meta.ResolveColumn(p.Columns, req.Column)
                       ?? throw new InvalidOperationException(
                           $"Column {req.Column} does not exist in {p.Table.Full}");
            p.Match = _meta.ResolveColumn(p.Columns, req.MatchColumn)
                      ?? throw new InvalidOperationException(
                          $"Match column {req.MatchColumn} does not exist in {p.Table.Full}");

            if (p.Column == p.Match)
            {
                throw new InvalidOperationException(
                    "The match column and the column being repaired cannot be the same one");
            }
            if (p.Column.IsPrimaryKey)
            {
                throw new InvalidOperationException($"Cannot edit primary key column {p.Column.Name}");
            }
            if (p.Column.IsReadOnly)
            {
                throw new InvalidOperationException(
                    $"Column {p.Column.Name} is identity/computed and cannot be edited");
            }
            if (p.Column.IsSkipped)
            {
                throw new InvalidOperationException(
                    $"Column {p.Column.Name} (type {p.Column.DataType}) cannot be written through the tool");
            }
            if (p.Match.IsSkipped)
            {
                throw new InvalidOperationException(
                    $"Column {p.Match.Name} (type {p.Match.DataType}) cannot be used to match rows");
            }

            // ---- source: read-only, on its own connection
            var srcMeta = MetaFor(scope);
            var wantedTable = req.SourceTable.IsEmpty() ? req.TargetTable : req.SourceTable;
            var wantedColumn = req.SourceColumn.IsEmpty() ? req.Column : req.SourceColumn;
            var wantedMatch = req.SourceMatchColumn.IsEmpty() ? req.MatchColumn : req.SourceMatchColumn;

            p.SourceTable = await srcMeta.ResolveTableAsync(wantedTable)
                            ?? throw new InvalidOperationException(
                                $"Table \"{wantedTable}\" not found in module \"{source.Name}\"");

            var sourceColumns = await srcMeta.GetColumnsAsync(p.SourceTable);

            p.SourceColumn = srcMeta.ResolveColumn(sourceColumns, wantedColumn)
                             ?? throw new InvalidOperationException(
                                 $"Column {wantedColumn} does not exist in {source.Name}.{p.SourceTable.Full}");
            p.SourceMatch = srcMeta.ResolveColumn(sourceColumns, wantedMatch)
                            ?? throw new InvalidOperationException(
                                $"Match column {wantedMatch} does not exist in {source.Name}.{p.SourceTable.Full}");

            if (p.SourceColumn == p.SourceMatch)
            {
                throw new InvalidOperationException(
                    "The match column and the column being read cannot be the same one on the source side");
            }
            if (p.SourceColumn.IsSkipped || p.SourceMatch.IsSkipped)
            {
                throw new InvalidOperationException("A binary or spatial column cannot take part in a sync");
            }

            CheckValueTypes(p.SourceColumn, p.Column, warn);
            CheckMatchTypes(p.SourceMatch, p.Match);

            // Named explicitly, or guessed: the audit log has to say WHICH row changed in terms a
            // person recognises, and a primary key here is usually a GUID.
            p.Label = req.LabelColumn.IsEmpty()
                ? _meta.GuessLabelColumn(p.Columns)
                : _meta.ResolveColumn(p.Columns, req.LabelColumn)
                  ?? throw new InvalidOperationException(
                      $"Column {req.LabelColumn} does not exist in {p.Table.Full}");

            p.Result.SourceModule = source.Key;
            p.Result.SourceModuleName = source.Name;
            p.Result.SourceTable = p.SourceTable.Full;
            p.Result.TargetTable = p.Table.Full;
            p.Result.Column = p.Column.Name;
            p.Result.SourceColumn = p.SourceColumn.Name;
            p.Result.MatchColumn = p.Match.Name;
            p.Result.SourceMatchColumn = p.SourceMatch.Name;
            p.Result.LabelColumn = p.Label?.Name ?? "";
            p.Result.CanApply = _module.CanEdit && req.Mode != SyncMode.CompareOnly;

            // ---- 1. candidate rows on the target
            var rows = await ReadTargetAsync(req, p);
            p.Result.Scanned = rows.Count;

            if (p.Result.Truncated)
            {
                warn.Add($"Chạm trần {Cap(req)} dòng — vẫn còn dòng chưa được xét. "
                         + "Thêm điều kiện lọc rồi chạy tiếp cho hết.");
            }

            // ---- 2. look those match values up in the source
            var (values, ambiguous) = await ReadSourceAsync(rows, p, warn);

            // ---- 3. classify, in memory
            Classify(rows, values, ambiguous, p);

            return p;
        }

        private int Cap(SyncPlanRequest req)
            => Math.Clamp(req.Take <= 0 ? _opt.Sync.MaxRows : req.Take, 1, _opt.Sync.MaxRows);

        private async Task<List<Dictionary<string, object?>>> ReadTargetAsync(SyncPlanRequest req, Prepared p)
        {
            var wanted = new List<ColumnInfo>();
            void Want(ColumnInfo c) { if (!wanted.Contains(c)) wanted.Add(c); }

            p.Keys.ForEach(Want);
            Want(p.Match);
            Want(p.Column);
            if (p.Label != null) Want(p.Label);

            var and = new List<string>();
            var ps = new List<KeyValuePair<string, object?>>();
            int i = 0;

            // FillEmpty narrows the scan itself; the other modes have to look at every row in range,
            // because "differs from the source" is not something SQL can evaluate on one server.
            if (req.Mode == SyncMode.FillEmpty) and.Add(EmptyCondition(p.Column));

            foreach (var f in req.Filters)
            {
                var c = _meta.ResolveColumn(p.Columns, f.Column);
                if (c == null || c.IsSkipped) continue;
                SqlFilter.Add(and, ps, ref i, c, f.Op, f.Value, _opt.SearchCollation);
            }

            int cap = Cap(req);
            var sql = "SELECT " + string.Join(", ", wanted.Select(c => "[" + c.Name + "]"))
                      + "\nFROM " + p.Table.Quoted
                      + (and.Count == 0 ? "" : "\nWHERE " + string.Join("\n  AND ", and))
                      + "\nORDER BY " + string.Join(", ", p.Keys.Select(k => "[" + k.Name + "]"))
                      // One row past the cap, so the reader can tell "exactly full" from "there is more".
                      + "\nOFFSET 0 ROWS FETCH NEXT " + (cap + 1) + " ROWS ONLY";

            p.Result.TargetSql = sql;

            var (_, rows, truncated) = await DbHelper.ExecAsync(_module.Connection, NoLock + sql,
                _opt.QueryTimeoutSeconds, ps, cmd => DbHelper.ReadAllAsync(cmd, cap));

            p.Result.Truncated = truncated;
            return rows;
        }

        /// <summary>
        /// Reads the source column for every match value found on the target, KeyChunk at a time.
        /// Returns the value per match key, plus the keys whose source rows disagree with each other.
        /// </summary>
        private async Task<(Dictionary<string, object?> values, HashSet<string> ambiguous)> ReadSourceAsync(
            List<Dictionary<string, object?>> rows, Prepared p, List<string> warn)
        {
            // Match keys are compared the way SQL Server itself would: case-insensitively and ignoring
            // trailing spaces. Anything stricter reports rows as missing that a JOIN would have paired.
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var keys = rows.Select(r => r.GetValueOrDefault(p.Match.Name))
                           .Where(v => v != null)
                           .Distinct()
                           .ToList();

            if (keys.Count == 0) return (values, ambiguous);

            var conn = p.SourceConnection!;
            bool opened = false;
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync();
                opened = true;
            }

            try
            {
                for (int i = 0; i < keys.Count; i += KeyChunk)
                {
                    var chunk = keys.Skip(i).Take(KeyChunk).ToList();
                    var names = new List<string>();
                    var ps = new List<KeyValuePair<string, object?>>();

                    for (int j = 0; j < chunk.Count; j++)
                    {
                        var name = "@k" + j;
                        names.Add(name);
                        // Values read from the target have been through SqlValue.Read, so they are
                        // strings by now; convert them back or SQL Server converts on every row.
                        ps.Add(new(name, SqlValue.Write(p.SourceMatch,
                            Convert.ToString(chunk[j], CultureInfo.InvariantCulture))));
                    }

                    var sql = "SELECT [" + p.SourceMatch.Name + "], [" + p.SourceColumn.Name + "]"
                              + "\nFROM " + p.SourceTable.Quoted
                              + "\nWHERE [" + p.SourceMatch.Name + "] IN (" + string.Join(", ", names) + ")";

                    if (p.Result.SourceSql.IsEmpty()) p.Result.SourceSql = sql;

                    // A duplicated match value can return far more rows than the chunk holds keys —
                    // cap it, and say so rather than silently reading only part of the answer.
                    var (_, sourceRows, cut) = await DbHelper.ExecAsync(conn, NoLock + sql,
                        _opt.QueryTimeoutSeconds, ps,
                        cmd => DbHelper.ReadAllAsync(cmd, _opt.Sync.MaxRows));

                    if (cut)
                    {
                        warn.Add($"Bảng nguồn trả về hơn {_opt.Sync.MaxRows} dòng cho một lô khóa — "
                                 + $"cột {p.SourceMatch.Name} bên nguồn trùng lặp nhiều, kết quả có thể thiếu.");
                    }

                    foreach (var r in sourceRows)
                    {
                        var k = Norm(r.GetValueOrDefault(p.SourceMatch.Name));
                        var v = r.GetValueOrDefault(p.SourceColumn.Name);

                        if (values.TryGetValue(k, out var seen))
                        {
                            if (!SameValue(seen, v)) ambiguous.Add(k);
                        }
                        else
                        {
                            values[k] = v;
                        }
                    }
                }
            }
            finally
            {
                if (opened) await conn.CloseAsync();
            }

            return (values, ambiguous);
        }

        private static void Classify(List<Dictionary<string, object?>> rows,
            Dictionary<string, object?> values, HashSet<string> ambiguous, Prepared p)
        {
            foreach (var r in rows)
            {
                var matchRaw = r.GetValueOrDefault(p.Match.Name);
                var current = r.GetValueOrDefault(p.Column.Name);

                var row = new SyncRow
                {
                    Key = p.Keys.ToDictionary(k => k.Name, k => Str(r.GetValueOrDefault(k.Name))),
                    Match = Str(matchRaw),
                    Label = p.Label == null ? null : Str(r.GetValueOrDefault(p.Label.Name)),
                    Current = Str(current)
                };

                if (matchRaw == null)
                {
                    row.State = SyncRowState.NotFound;
                    row.Note = "Khóa ghép của dòng này đang trống";
                }
                else if (ambiguous.Contains(Norm(matchRaw)))
                {
                    row.State = SyncRowState.Ambiguous;
                    row.Note = "Bên nguồn có nhiều dòng cùng khóa nhưng khác giá trị";
                }
                else if (!values.TryGetValue(Norm(matchRaw), out var incoming))
                {
                    row.State = SyncRowState.NotFound;
                }
                else
                {
                    row.Incoming = Str(incoming);

                    if (IsBlank(incoming)) row.State = SyncRowState.SourceEmpty;
                    else if (SameValue(current, incoming)) row.State = SyncRowState.Same;
                    else if (IsBlank(current)) row.State = SyncRowState.Fill;
                    else row.State = SyncRowState.Overwrite;

                    if (row.State is SyncRowState.Fill or SyncRowState.Overwrite)
                    {
                        var problem = WriteProblem(p.Column, row.Incoming);
                        if (problem != null)
                        {
                            row.State = SyncRowState.Blocked;
                            row.Note = problem;
                        }
                    }
                }

                p.Result.Rows.Add(row);
            }
        }

        // ---------------------------------------------------------------- apply

        public async Task<SyncApplyResult> ApplyAsync(SyncApplyRequest req, ModuleDefinition source,
            string username, string? ip)
        {
            if (req.Mode == SyncMode.CompareOnly)
            {
                throw new InvalidOperationException("This run is set to compare only and cannot write");
            }
            if (!_module.CanEdit)
            {
                throw new InvalidOperationException("Editing is disabled for this module");
            }
            if (req.Keys.Count == 0)
            {
                throw new InvalidOperationException("No rows were selected");
            }

            var watch = Stopwatch.StartNew();
            await using var scope = _scopes.Open(source.Key);

            // Rebuilt from scratch rather than trusting what came back from the browser: the value to
            // write is decided here, and the data may have moved since the preview was drawn.
            var prep = await PrepareAsync(req, source, scope);
            var result = new SyncApplyResult();

            var byKey = new Dictionary<string, SyncRow>(StringComparer.Ordinal);
            foreach (var row in prep.Result.Rows) byKey[Signature(row.Key)] = row;

            var todo = new List<SyncRow>();
            foreach (var key in req.Keys)
            {
                if (!byKey.TryGetValue(Signature(key), out var row))
                {
                    result.Rows.Add(new SyncRow
                    {
                        Key = key,
                        State = SyncRowState.Stale,
                        Note = "Dòng không còn trong kết quả xem trước — bỏ qua"
                    });
                    result.Skipped++;
                    continue;
                }

                // The mode is enforced here, not only in the UI: a request naming rows that would
                // overwrite real data must not succeed while the run says "chỉ điền vào ô trống".
                bool eligible = row.State == SyncRowState.Fill
                                || (row.State == SyncRowState.Overwrite && req.Mode == SyncMode.FillAndOverwrite);

                if (!eligible)
                {
                    row.Note ??= "Dòng không còn ở trạng thái ghi được — bỏ qua";
                    result.Rows.Add(row);
                    result.Skipped++;
                    continue;
                }

                todo.Add(row);
            }

            // One id for the whole run, so the per-row entries can be read back together later and
            // turned into an undo script. Without it they are indistinguishable from any other edit.
            var runId = Guid.NewGuid();

            if (todo.Count > 0) await WriteAsync(todo, prep, source, username, ip, result, runId);

            await _audit.WriteAsync(new AuditLogEntry
            {
                Type = "sync",
                User = username,
                Ip = ip,
                RunId = runId,
                TableName = prep.Table.Full,
                Sql = result.Sql,
                Note =
                    $"{prep.Table.Full}.{prep.Column.Name} ← {source.Key}.{prep.SourceTable.Full}.{prep.SourceColumn.Name}"
                    + $" · ghép theo {prep.Match.Name} · ghi {result.Updated} / bỏ qua {result.Skipped}"
            });

            result.RunId = runId;
            result.ElapsedMs = watch.ElapsedMilliseconds;
            return result;
        }

        private async Task WriteAsync(List<SyncRow> todo, Prepared p, ModuleDefinition source,
            string username, string? ip, SyncApplyResult result, Guid runId)
        {
            var where = string.Join(" AND ", p.Keys.Select((k, i) => "[" + k.Name + "] = @k" + i));
            var note = "sync ← " + source.Key + "." + p.SourceTable.Full + "." + p.SourceColumn.Name;

            var conn = _module.Connection;
            bool opened = false;
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync();
                opened = true;
            }

            try
            {
                for (int i = 0; i < todo.Count; i += _opt.Sync.BatchSize)
                {
                    var chunk = todo.Skip(i).Take(_opt.Sync.BatchSize).ToList();
                    var log = new List<AuditLogEntry>();

                    await using var tran = await conn.BeginTransactionAsync();

                    foreach (var row in chunk)
                    {
                        // Optimistic guard: the row is written only while it still holds the value the
                        // preview showed. Somebody else's edit in between makes the UPDATE match nothing,
                        // which is reported back — never silently overwritten.
                        //
                        // The comparison value comes from the same string round-trip the grid uses, so a
                        // column with finer precision than that string keeps (datetime2(7), say) can fail
                        // to match its own unchanged value and be reported as skipped. That only affects
                        // rows being overwritten — a Fill row guards on IS NULL — and erring towards "did
                        // not write" is the right direction for a guard to fail in.
                        var guard = row.Current == null
                            ? Comparable(p.Column) + " IS NULL"
                            : Comparable(p.Column) + " = @old";

                        var sql = "UPDATE " + p.Table.Quoted
                                  + " SET [" + p.Column.Name + "] = @v"
                                  + " WHERE " + where + " AND " + guard;

                        if (result.Sql.IsEmpty()) result.Sql = sql;

                        var ps = new List<KeyValuePair<string, object?>>
                        {
                            new("@v", SqlValue.Write(p.Column, row.Incoming))
                        };

                        for (int j = 0; j < p.Keys.Count; j++)
                        {
                            ps.Add(new("@k" + j,
                                SqlValue.Write(p.Keys[j], row.Key.GetValueOrDefault(p.Keys[j].Name))));
                        }

                        if (row.Current != null) ps.Add(new("@old", SqlValue.Write(p.Column, row.Current)));

                        int affected;
                        await using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = tran;
                            cmd.CommandText = sql;
                            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
                            DbHelper.AddParams(cmd, ps);
                            affected = await cmd.ExecuteNonQueryAsync();
                        }

                        if (affected == 1)
                        {
                            row.State = SyncRowState.Updated;
                            row.Note = null;
                            result.Updated++;

                            log.Add(new AuditLogEntry
                            {
                                Type = "update",
                                User = username,
                                Ip = ip,
                                RunId = runId,
                                TableName = p.Table.Full,
                                KeyJson = JsonConvert.SerializeObject(row.Key),
                                Label = row.Label,
                                ChangesJson = JsonConvert.SerializeObject(new[]
                                {
                                    new ChangeLog
                                    {
                                        Column = p.Column.Name,
                                        From = row.Current,
                                        To = row.Incoming
                                    }
                                }),
                                Sql = sql,
                                Note = note
                            });
                        }
                        else if (affected == 0)
                        {
                            row.State = SyncRowState.Stale;
                            row.Note = "Dòng đã bị đổi từ lúc xem trước — bỏ qua";
                            result.Skipped++;
                        }
                        else
                        {
                            // The WHERE is the primary key, so this cannot happen against sane metadata.
                            // If it does, the key is not unique and every assumption here is off.
                            await tran.RollbackAsync();
                            throw new InvalidOperationException(
                                $"One statement matched {affected} rows instead of 1 — this batch was "
                                + $"rolled back and the run stopped. The {result.Updated} row(s) written "
                                + "by earlier batches stay written.");
                        }

                        result.Rows.Add(row);
                    }

                    await tran.CommitAsync();

                    // Logged after the commit, so the log can never claim a change that was rolled back.
                    await _audit.WriteManyAsync(log);
                }
            }
            finally
            {
                if (opened) await conn.CloseAsync();
            }
        }

        // -------------------------------------------------------------- helpers

        /// <summary>
        /// "Empty" for the purposes of this page: NULL, or a string holding nothing but spaces. Real
        /// data mixes the two, and treating '' as filled would leave half the damage behind.
        /// </summary>
        private static string EmptyCondition(ColumnInfo c)
        {
            var col = "[" + c.Name + "]";
            if (!c.IsText) return col + " IS NULL";

            // text/ntext/xml cannot be trimmed or compared, so ask for their length instead.
            return LegacyText.Contains(c.DataType)
                ? "(" + col + " IS NULL OR DATALENGTH(" + col + ") = 0)"
                : "(" + col + " IS NULL OR LTRIM(RTRIM(" + col + ")) = '')";
        }

        /// <summary>text/ntext/xml cannot appear next to "=" at all — cast them first.</summary>
        private static string Comparable(ColumnInfo c)
            => LegacyText.Contains(c.DataType)
                ? "CAST([" + c.Name + "] AS nvarchar(max))"
                : "[" + c.Name + "]";

        private static bool IsBlank(object? v)
            => v == null || (v is string s && s.Trim().Length == 0);

        /// <summary>
        /// Trailing spaces are dropped before comparing, the way SQL Server compares strings itself —
        /// otherwise a column padded by an old import reads as different on every single row. Case,
        /// on the other hand, is significant: replacing "ABC" with "abc" is a real change.
        /// </summary>
        private static bool SameValue(object? a, object? b)
        {
            if (a == null || b == null) return a == null && b == null;
            return string.Equals(Text(a), Text(b), StringComparison.Ordinal);
        }

        private static string Text(object? v)
            => (Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").TrimEnd();

        private static string Norm(object? v) => Text(v);

        private static string? Str(object? v)
            => v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);

        /// <summary>Joins the parts of a key signature — a character no column value can contain.</summary>
        private const string Separator = "\u0001";

        /// <summary>
        /// Stable identity of a primary key, so a ticked row can be found again after re-planning.
        /// Without a separator a two-column key could be confused with a different one that happens
        /// to concatenate the same way.
        /// </summary>
        private static string Signature(Dictionary<string, string?> key)
            => string.Join(Separator, key.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                                        .Select(k => k.Key.ToLowerInvariant() + "=" + k.Value));

        /// <summary>Why this value could not be written — null when it can.</summary>
        private static string? WriteProblem(ColumnInfo target, string? value)
        {
            if (value == null) return null;

            try
            {
                var converted = SqlValue.Write(target, value);
                if (converted == null && !target.IsNullable) return "Cột không cho phép NULL";
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }

            if (target.IsText && target.MaxLength >= 0 && value.Length > target.MaxLength)
            {
                return $"Giá trị dài {value.Length} ký tự, cột chỉ chứa được {target.MaxLength}";
            }

            return null;
        }

        private static void CheckValueTypes(ColumnInfo src, ColumnInfo dst, List<string> warn)
        {
            var from = Family(src);
            var to = Family(dst);
            if (from == to) return;

            // Everything has a text form, so a text target is always reachable — just worth saying.
            if (to == TypeFamily.Text)
            {
                warn.Add($"Cột nguồn kiểu {src.DataType}, cột đích kiểu {dst.DataType} — "
                         + "giá trị sẽ được ghi ở dạng chuỗi.");
                return;
            }

            throw new InvalidOperationException(
                $"Column {src.Name} ({src.DataType}) and column {dst.Name} ({dst.DataType}) hold "
                + "different kinds of value — refusing to copy between them");
        }

        private static void CheckMatchTypes(ColumnInfo src, ColumnInfo dst)
        {
            if (Family(src) == Family(dst)) return;

            throw new InvalidOperationException(
                $"Match columns {dst.Name} ({dst.DataType}) and {src.Name} ({src.DataType}) hold "
                + "different kinds of value, so rows cannot be paired up by them");
        }

        private enum TypeFamily { Text, Integer, Decimal, Date, Time, Guid, Bool, Other }

        private static TypeFamily Family(ColumnInfo c) => c.DataType.ToLowerInvariant() switch
        {
            "char" or "nchar" or "varchar" or "nvarchar" or "text" or "ntext" or "xml" => TypeFamily.Text,
            "tinyint" or "smallint" or "int" or "bigint" => TypeFamily.Integer,
            "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => TypeFamily.Decimal,
            "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" => TypeFamily.Date,
            "time" => TypeFamily.Time,
            "uniqueidentifier" => TypeFamily.Guid,
            "bit" => TypeFamily.Bool,
            _ => TypeFamily.Other
        };

        /// <summary>Both sides resolved, plus the result being built. Shared by Plan and Apply.</summary>
        private sealed class Prepared
        {
            public TableInfo Table = null!;
            public List<ColumnInfo> Columns = null!;
            public List<ColumnInfo> Keys = null!;
            public ColumnInfo Column = null!;
            public ColumnInfo Match = null!;

            /// <summary>Null when the table holds nothing a person would recognise a row by.</summary>
            public ColumnInfo? Label;

            public TableInfo SourceTable = null!;
            public ColumnInfo SourceColumn = null!;
            public ColumnInfo SourceMatch = null!;

            public DbConnection? SourceConnection;

            public SyncPlanResult Result = null!;
        }
    }
}

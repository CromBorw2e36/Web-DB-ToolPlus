using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using ToolPlus.Common;
using ToolPlus.Models.Copy;
using ToolPlus.Models.Explorer;
using ToolPlus.Models.ToolEntity;
using ToolPlus.Services.Explorer;
using ToolPlus.Services.Interface;

namespace ToolPlus.Services.Copy
{
    /// <summary>
    /// Brings tables of this module in line with the same tables in another one — the refresh of a
    /// backup or test copy that would otherwise mean a full backup and restore.
    ///
    /// Both databases are walked in primary-key order, one page at a time, and paired in memory: the
    /// two are usually on different servers, and a business database must stay clean of this tool's
    /// footprint, so there is no cross-database JOIN and no staging table. Each source page fixes a
    /// key range; the target is read over exactly that range, so what is missing, what differs and
    /// what is extra can all be told apart without either side ever being held in memory whole.
    ///
    /// As in SyncService the direction is fixed: the target is always the module in the URL.
    /// </summary>
    public class TableCopyService : ITableCopyService
    {
        /// <summary>Both sides read dirty — this tool must never hold a lock on a production database.</summary>
        private const string NoLock = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;\n";

        /// <summary>SQL Server allows 2100 parameters per command; keep clear of it.</summary>
        private const int MaxParams = 2000;

        /// <summary>Statements per round trip when updating or deleting row by row.</summary>
        private const int MaxStatements = 200;

        /// <summary>
        /// CLR types SqlClient cannot read without Microsoft.SqlServer.Types. Skipping such a column
        /// would insert rows with it empty, so a table holding one is refused instead.
        /// </summary>
        private static readonly HashSet<string> Unsupported = new(StringComparer.OrdinalIgnoreCase)
            { "geography", "geometry", "hierarchyid" };

        private const string Separator = "\u0001";

        private readonly IModuleContext _module;
        private readonly IMetadataService _meta;
        private readonly IModuleScopeFactory _scopes;
        private readonly MemoryCacheManager _cache;
        private readonly IAuditService _audit;
        private readonly ToolPlusOptions _opt;

        public TableCopyService(IModuleContext module, IMetadataService meta, IModuleScopeFactory scopes,
            MemoryCacheManager cache, IAuditService audit, IOptions<ToolPlusOptions> opt)
        {
            _module = module;
            _meta = meta;
            _scopes = scopes;
            _cache = cache;
            _audit = audit;
            _opt = opt.Value;
        }

        private CopyOptions Copy => _opt.Copy;

        /// <summary>The whitelist layer pointed at the source database — see SyncService.MetaFor.</summary>
        private IMetadataService MetaFor(IModuleScope scope) => new MetadataService(scope, _cache);

        private bool CanApply => _module.CanEdit && _module.Current.CopyTarget;

        // --------------------------------------------------------------- tables

        public async Task<List<CopyTableStat>> TablesAsync(ModuleDefinition source)
        {
            EnsureDifferent(source);

            var tables = (await _meta.GetTablesAsync()).Where(t => !t.IsView).ToList();
            var here = await StatsAsync(_module, null);

            await using var scope = _scopes.Open(source.Key);
            var there = await StatsAsync(scope, null);

            return tables.Select(t =>
            {
                var h = here.GetValueOrDefault(t.Full);
                var found = there.TryGetValue(t.Full, out var s);
                return new CopyTableStat
                {
                    Full = t.Full,
                    HasPrimaryKey = h.HasPk,
                    InSource = found,
                    TargetRows = h.Rows ?? -1,
                    SourceRows = found ? s.Rows ?? -1 : -1
                };
            }).ToList();
        }

        /// <summary>
        /// Row counts from sys.partitions and whether each table has a primary key. Metadata only, so
        /// it is instant even on tables a COUNT(*) would take minutes over.
        /// </summary>
        private static async Task<Dictionary<string, (long? Rows, bool HasPk)>> StatsAsync(IModuleScope scope,
            TableInfo? only)
        {
            var sql = @"
SELECT s.name + '.' + o.name AS FullName,
       SUM(CASE WHEN p.index_id IN (0, 1) THEN p.rows ELSE 0 END) AS RowCnt,
       CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.key_constraints k
                              WHERE k.parent_object_id = o.object_id AND k.type = 'PK')
                 THEN 1 ELSE 0 END AS bit) AS HasPk
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
LEFT JOIN sys.partitions p ON p.object_id = o.object_id
WHERE o.type = 'U' AND o.is_ms_shipped = 0"
                      + (only == null ? "" : " AND o.object_id = OBJECT_ID(@obj)")
                      + "\nGROUP BY s.name, o.name, o.object_id";

            var ps = only == null ? null : new[] { new KeyValuePair<string, object?>("@obj", only.Quoted) };

            return await DbHelper.ExecAsync(scope.Connection, sql, 60, ps, async cmd =>
            {
                var result = new Dictionary<string, (long?, bool)>(StringComparer.OrdinalIgnoreCase);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    long? rows = reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1));
                    result[reader.GetString(0)] = (rows, reader.GetBoolean(2));
                }
                return result;
            });
        }

        // ---------------------------------------------------------------- order

        public async Task<CopyOrderResult> OrderAsync(List<string> tables)
        {
            var result = new CopyOrderResult();

            var picked = new List<string>();
            foreach (var name in tables)
            {
                var t = await _meta.ResolveTableAsync(name);
                if (t != null && !picked.Contains(t.Full, StringComparer.OrdinalIgnoreCase)) picked.Add(t.Full);
            }

            var set = new HashSet<string>(picked, StringComparer.OrdinalIgnoreCase);
            var fks = await _meta.GetForeignKeysAsync();

            var parents = picked.ToDictionary(t => t, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

            foreach (var fk in fks)
            {
                if (!set.Contains(fk.ChildTable) || !set.Contains(fk.ParentTable)) continue;

                if (string.Equals(fk.ChildTable, fk.ParentTable, StringComparison.OrdinalIgnoreCase))
                {
                    var w = $"{fk.ChildTable} tự tham chiếu ({fk.ChildColumn} → {fk.ParentColumn}) — nếu báo lỗi khóa ngoại, chạy lại lần nữa sẽ qua.";
                    if (!result.Warnings.Contains(w)) result.Warnings.Add(w);
                    continue;
                }

                parents[fk.ChildTable].Add(fk.ParentTable);
            }

            // Kahn's algorithm, always taking the earliest remaining table in the user's own order, so
            // tables with no relation to each other keep the order they were picked in.
            var remaining = new List<string>(picked);
            while (remaining.Count > 0)
            {
                var next = remaining.FirstOrDefault(t =>
                    parents[t].All(p => result.Tables.Contains(p, StringComparer.OrdinalIgnoreCase)));

                if (next == null)
                {
                    result.Warnings.Add("Các bảng " + string.Join(", ", remaining)
                                        + " tham chiếu vòng tròn lẫn nhau — sẽ chạy theo thứ tự đã chọn.");
                    result.Tables.AddRange(remaining);
                    break;
                }

                result.Tables.Add(next);
                remaining.Remove(next);
            }

            return result;
        }

        // -------------------------------------------------------------- compare

        public async Task<CopyCompareStep> CompareAsync(CopyCompareRequest req, ModuleDefinition source,
            CancellationToken ct)
        {
            var watch = Stopwatch.StartNew();
            await using var scope = _scopes.Open(source.Key);

            bool first = req.Cursor == null;
            var p = await PrepareAsync(req.Table, source, scope, checkSize: first);

            var step = new CopyCompareStep
            {
                Table = p.Table.Full,
                Keys = p.KeyIdx.Select(i => p.Cols[i].Name).ToList(),
                Columns = p.Cols.Select(c => c.Name).ToList(),
                LabelColumn = p.LabelIdx < 0 ? "" : p.Cols[p.LabelIdx].Name,
                SourceApprox = p.SourceApprox,
                TargetApprox = p.TargetApprox,
                CanApply = CanApply,
                Warnings = first ? p.Warnings : new List<string>()
            };

            int listIns = 0, listUpd = 0, listDel = 0;
            int cap = Math.Max(0, Copy.MaxListedRows);

            await using (await OpenBothAsync(p, ct))
            {
                var next = await ScanAsync(p, ParseCursor(p, req.Cursor), watch, chunk =>
                {
                    step.SourceScanned += chunk.SourceScanned;
                    step.TargetScanned += chunk.TargetScanned;
                    step.Same += chunk.Same;
                    step.Insert += chunk.Inserts.Count;
                    step.Update += chunk.Updates.Count;
                    step.Delete += chunk.Deletes.Count;

                    foreach (var r in chunk.Inserts)
                    {
                        if (listIns++ >= cap) break;
                        step.Rows.Add(new CopyDiffRow
                        {
                            Kind = CopyKind.Insert, Key = KeyDict(p, r), Label = LabelOf(p, r), Values = ValuesOf(p, r)
                        });
                    }
                    foreach (var u in chunk.Updates)
                    {
                        if (listUpd++ >= cap) break;
                        step.Rows.Add(new CopyDiffRow
                        {
                            Kind = CopyKind.Update,
                            Key = KeyDict(p, u.Tgt),
                            Label = LabelOf(p, u.Tgt),
                            Changes = u.Columns.Select(i => new ChangeLog
                            {
                                Column = p.Cols[i].Name, From = Disp(u.Tgt[i]), To = Disp(u.Src[i])
                            }).ToList()
                        });
                    }
                    foreach (var r in chunk.Deletes)
                    {
                        if (listDel++ >= cap) break;
                        step.Rows.Add(new CopyDiffRow
                        {
                            Kind = CopyKind.Delete, Key = KeyDict(p, r), Label = LabelOf(p, r), Values = ValuesOf(p, r)
                        });
                    }

                    return Task.CompletedTask;
                }, ct);

                step.Cursor = CursorOut(next);
            }

            step.ElapsedMs = watch.ElapsedMilliseconds;
            return step;
        }

        // ---------------------------------------------------------------- apply

        public async Task<CopyApplyStep> ApplyAsync(CopyApplyRequest req, ModuleDefinition source,
            string username, string? ip, CancellationToken ct)
        {
            if (!_module.Current.CopyTarget)
            {
                throw new InvalidOperationException(
                    $"Module \"{_module.Current.Name}\" chưa được bật làm đích chép bảng — đặt "
                    + $"ToolPlus:Modules:{_module.Current.ConnectionKey}:CopyTarget = true nếu đây đúng là database được phép ghi đè.");
            }
            if (!_module.CanEdit)
            {
                throw new InvalidOperationException("Database này đang ở chế độ chỉ đọc với tài khoản của bạn");
            }

            var watch = Stopwatch.StartNew();
            var result = new CopyApplyStep { RunId = req.RunId == Guid.Empty ? Guid.NewGuid() : req.RunId };

            // A phase with nothing allowed has nothing to do. Returned rather than refused, so the
            // browser can walk every table through both phases without special-casing.
            bool doDelete = req.Phase == CopyPhase.Delete && req.Delete;
            bool doUpdate = req.Phase == CopyPhase.Upsert && req.Update;
            bool doInsert = req.Phase == CopyPhase.Upsert && req.Insert;
            bool keyed = req.Keys != null;

            if ((!doDelete && !doUpdate && !doInsert) || (keyed && req.Keys!.Count == 0))
            {
                return result;
            }

            await using var scope = _scopes.Open(source.Key);
            var p = await PrepareAsync(req.Table, source, scope, checkSize: !keyed && req.Cursor == null);

            string? sample = null;
            var note = "copy ← " + source.Key + "." + p.SourceTable.Full;

            try
            {
                await using (await OpenBothAsync(p, ct))
                {
                    // The one mistake a mirror run cannot survive: the wrong source database, or a
                    // source table that is empty right now, turns "delete what is extra" into "delete
                    // everything". Rows ticked one by one were seen by a person and are still allowed.
                    if (doDelete && !keyed && req.Cursor == null && !await SourceHasRowsAsync(p, ct))
                    {
                        throw new InvalidOperationException(
                            $"Bảng nguồn {source.Name}.{p.SourceTable.Full} đang rỗng — từ chối xoá sạch "
                            + "bảng đích. Nếu thật sự muốn, tick tay các dòng cần xoá.");
                    }

                    async Task Write(Chunk chunk)
                    {
                        result.SourceScanned += chunk.SourceScanned;

                        var dels = doDelete ? chunk.Deletes : new List<object?[]>();
                        var ups = doUpdate ? chunk.Updates : new List<Changed>();
                        var ins = doInsert ? chunk.Inserts : new List<object?[]>();
                        if (dels.Count == 0 && ups.Count == 0 && ins.Count == 0) return;

                        var log = new List<AuditLogEntry>();
                        var tran = (SqlTransaction)await p.Target.BeginTransactionAsync(ct);
                        try
                        {
                            var batch = new GuardedBatch(p.Target, tran, _opt.QueryTimeoutSeconds);

                            foreach (var row in dels)
                            {
                                await batch.FlushIfFullAsync(p.KeyIdx.Length, ct);
                                var sql = "DELETE FROM " + p.Table.Quoted + " WHERE " + KeyEquals(p, row, batch);
                                sample ??= sql;
                                batch.Add(sql);

                                log.Add(new AuditLogEntry
                                {
                                    Type = "copy-delete",
                                    User = username,
                                    Ip = ip,
                                    RunId = result.RunId,
                                    TableName = p.Table.Full,
                                    KeyJson = JsonConvert.SerializeObject(KeyDict(p, row)),
                                    Label = LabelOf(p, row),
                                    // The whole row, because it exists nowhere else once it is gone.
                                    ChangesJson = JsonConvert.SerializeObject(p.Cols.Select((c, i) =>
                                        new ChangeLog { Column = c.Name, From = Str(row[i]), To = null })),
                                    Sql = sql,
                                    Note = note
                                });
                            }

                            foreach (var u in ups)
                            {
                                await batch.FlushIfFullAsync(u.Columns.Count + p.KeyIdx.Length, ct);
                                var set = string.Join(", ", u.Columns.Select(i =>
                                    Q(p.Cols[i]) + " = " + batch.P(p.Cols[i], u.Src[i])));
                                var sql = "UPDATE " + p.Table.Quoted + " SET " + set
                                          + " WHERE " + KeyEquals(p, u.Tgt, batch);
                                sample ??= sql;
                                batch.Add(sql);

                                // Logged as an ordinary update, so Explorer's history can already
                                // turn a copy run back into an undo script.
                                log.Add(new AuditLogEntry
                                {
                                    Type = "update",
                                    User = username,
                                    Ip = ip,
                                    RunId = result.RunId,
                                    TableName = p.Table.Full,
                                    KeyJson = JsonConvert.SerializeObject(KeyDict(p, u.Tgt)),
                                    Label = LabelOf(p, u.Tgt),
                                    ChangesJson = JsonConvert.SerializeObject(u.Columns.Select(i => new ChangeLog
                                    {
                                        Column = p.Cols[i].Name, From = Str(u.Tgt[i]), To = Str(u.Src[i])
                                    })),
                                    Sql = sql,
                                    Note = note
                                });
                            }

                            await batch.FlushAsync(ct);

                            if (ins.Count > 0)
                            {
                                sample ??= "INSERT BULK " + p.Table.Quoted;
                                await BulkInsertAsync(p, tran, ins, ct);
                            }

                            await tran.CommitAsync(ct);
                        }
                        catch
                        {
                            // The server may already have rolled back on its own (a constraint error in
                            // a bulk insert dooms the transaction), in which case this throws too.
                            try { await tran.RollbackAsync(CancellationToken.None); } catch { /* already gone */ }
                            throw;
                        }
                        finally
                        {
                            await tran.DisposeAsync();
                        }

                        result.Deleted += dels.Count;
                        result.Updated += ups.Count;
                        result.Inserted += ins.Count;

                        // After the commit, so the log never claims a change that was rolled back.
                        await _audit.WriteManyAsync(log);
                    }

                    if (keyed)
                    {
                        await ScanKeysAsync(p, req.Keys!, Write, ct);
                        result.Skipped = Math.Max(0, req.Keys!.Count - result.Inserted - result.Updated - result.Deleted);
                    }
                    else
                    {
                        var next = await ScanAsync(p, ParseCursor(p, req.Cursor), watch, Write, ct);
                        result.Cursor = CursorOut(next);
                    }
                }
            }
            catch (Exception ex)
            {
                int done = result.Inserted + result.Updated + result.Deleted;
                if (done > 0)
                {
                    // Best effort: the committed chunks must show up in the log, but a failure to log
                    // them must not replace the error the user actually needs to see.
                    try { await Summary(p, source, req, result, username, ip, sample, "DỪNG VÌ LỖI"); }
                    catch { /* the original error wins */ }
                }

                if (ex is OperationCanceledException) throw;

                var why = ex is InvalidOperationException ? ex.Message : ex.GetBaseException().Message;
                throw new InvalidOperationException(p.Table.Full + ": " + why
                    + (done > 0 ? $" — {done} dòng ở các lô trước đã ghi xong và được giữ nguyên." : ""));
            }

            if (result.Inserted + result.Updated + result.Deleted > 0)
            {
                await Summary(p, source, req, result, username, ip, sample, null);
            }

            result.ElapsedMs = watch.ElapsedMilliseconds;
            return result;
        }

        /// <summary>One entry per step. A big table takes several steps; the RunId ties them together.</summary>
        private Task Summary(Pair p, ModuleDefinition source, CopyApplyRequest req, CopyApplyStep r,
            string username, string? ip, string? sql, string? failure)
            => _audit.WriteAsync(new AuditLogEntry
            {
                Type = "copy",
                User = username,
                Ip = ip,
                RunId = r.RunId,
                TableName = p.Table.Full,
                Sql = sql,
                Note = $"{p.Table.Full} ← {source.Key}.{p.SourceTable.Full}"
                       + (req.Phase == CopyPhase.Delete ? " · xoá dòng thừa" : " · thêm/cập nhật")
                       + $" · thêm {r.Inserted} · cập nhật {r.Updated} · xoá {r.Deleted}"
                       + (req.Keys != null ? $" · chọn tay {req.Keys.Count} dòng" : "")
                       + (r.Cursor != null ? " · còn tiếp" : "")
                       + (failure != null ? " · " + failure : "")
            });

        private async Task<bool> SourceHasRowsAsync(Pair p, CancellationToken ct)
        {
            var rows = await ReadAsync(p.Source, "SELECT TOP 1 1 FROM " + p.SourceTable.Quoted,
                new List<SqlParameter>(), ct);
            return rows.Count > 0;
        }

        private async Task BulkInsertAsync(Pair p, SqlTransaction tran, List<object?[]> rows, CancellationToken ct)
        {
            var dt = new DataTable();
            for (int i = 0; i < p.Cols.Count; i++)
            {
                dt.Columns.Add(p.Cols[i].Name, p.SourceTypes?[i] ?? typeof(object));
            }
            foreach (var r in rows)
            {
                dt.Rows.Add(r.Select(v => v ?? DBNull.Value).ToArray());
            }

            // KeepIdentity: a copy that renumbers its rows is not a copy.
            // KeepNulls: a NULL in the source stays NULL rather than picking up the column default.
            // CheckConstraints: without it SQL Server marks every foreign key on the table as not
            //   trusted — a change to the database's own metadata, which this tool must never make.
            // Triggers are deliberately not fired, as they would not be by a restore either.
            using var bulk = new SqlBulkCopy(p.Target,
                SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls | SqlBulkCopyOptions.CheckConstraints,
                tran)
            {
                DestinationTableName = p.Table.Quoted,
                BulkCopyTimeout = _opt.QueryTimeoutSeconds
            };

            foreach (var c in p.Cols) bulk.ColumnMappings.Add(c.Name, c.Name);

            await bulk.WriteToServerAsync(dt, ct);
        }

        // -------------------------------------------------------------- prepare

        private async Task<Pair> PrepareAsync(string tableName, ModuleDefinition source, IModuleScope scope,
            bool checkSize)
        {
            EnsureDifferent(source);

            var table = await _meta.ResolveTableAsync(tableName)
                        ?? throw new InvalidOperationException($"Không có bảng \"{tableName}\"");
            if (table.IsView)
            {
                throw new InvalidOperationException($"{table.Full} là view — không ghi vào được");
            }

            var srcMeta = MetaFor(scope);
            var srcTable = await srcMeta.ResolveTableAsync(table.Full);
            if (srcTable == null || srcTable.IsView)
            {
                throw new InvalidOperationException($"Database nguồn \"{source.Name}\" không có bảng {table.Full}");
            }

            var cols = await _meta.GetColumnsAsync(table);
            var srcCols = await srcMeta.GetColumnsAsync(srcTable);
            var info = await ColumnInfoAsync(_module, table);
            var srcInfo = await ColumnInfoAsync(scope, srcTable);

            var p = new Pair { Table = table, SourceTable = srcTable };

            var keys = info.Where(kv => kv.Value.KeyOrdinal > 0)
                           .OrderBy(kv => kv.Value.KeyOrdinal)
                           .Select(kv => _meta.ResolveColumn(cols, kv.Key)!)
                           .Where(c => c != null)
                           .ToList();
            if (keys.Count == 0)
            {
                throw new InvalidOperationException($"Bảng {table.Full} không có khóa chính — không ghép dòng hai bên được");
            }

            var ordered = keys.Concat(cols.Where(c => !keys.Contains(c))).ToList();
            var missing = new List<string>();

            foreach (var c in ordered)
            {
                bool isKey = keys.Contains(c);

                if (Unsupported.Contains(c.DataType))
                {
                    throw new InvalidOperationException(
                        $"Cột {c.Name} kiểu {c.DataType} chưa được hỗ trợ — không chép được bảng {table.Full}");
                }

                // Computed and rowversion columns are produced by the server and cannot be written.
                if (c.IsReadOnly && !c.IsIdentity)
                {
                    if (isKey)
                    {
                        throw new InvalidOperationException(
                            $"Khóa chính {c.Name} là cột tính toán — không chép được bảng {table.Full}");
                    }
                    continue;
                }

                var sc = srcMeta.ResolveColumn(srcCols, c.Name);
                if (sc == null)
                {
                    if (isKey)
                    {
                        throw new InvalidOperationException(
                            $"Bảng nguồn không có cột khóa {c.Name} — không ghép dòng được");
                    }
                    missing.Add(c.Name);
                    continue;
                }

                if (Unsupported.Contains(sc.DataType))
                {
                    throw new InvalidOperationException(
                        $"Cột nguồn {sc.Name} kiểu {sc.DataType} chưa được hỗ trợ");
                }

                if (isKey)
                {
                    // Both sides are paged by range, each ordered by its own collation. If the two
                    // disagree on what comes after what, a range read on one side misses rows the other
                    // put inside it — and a mirror run would then delete rows that are really there.
                    var a = info.GetValueOrDefault(c.Name).Collation;
                    var b = srcInfo.GetValueOrDefault(sc.Name).Collation;
                    if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Cột khóa {c.Name} có collation khác nhau ({a} ↔ {b}) — không so sánh an toàn được");
                    }

                    p.CaseInsensitive.Add(a != null
                                          && !a.Contains("_CS", StringComparison.OrdinalIgnoreCase)
                                          && !a.Contains("_BIN", StringComparison.OrdinalIgnoreCase));
                }

                if (!string.Equals(c.DataType, sc.DataType, StringComparison.OrdinalIgnoreCase))
                {
                    p.Warnings.Add($"Cột {c.Name}: nguồn {sc.DataType}, đích {c.DataType} — giá trị sẽ được SQL Server chuyển kiểu.");
                }

                p.Cols.Add(c);
                p.SrcCols.Add(sc);
            }

            if (missing.Count > 0)
            {
                p.Warnings.Add("Bảng nguồn không có cột " + string.Join(", ", missing)
                               + " — không so sánh các cột này; dòng thêm mới sẽ để trống hoặc lấy giá trị mặc định.");
            }

            var extra = srcCols.Where(sc => _meta.ResolveColumn(cols, sc.Name) == null).Select(sc => sc.Name).ToList();
            if (extra.Count > 0)
            {
                p.Warnings.Add("Bảng nguồn có thêm cột " + string.Join(", ", extra.Take(8))
                               + (extra.Count > 8 ? "…" : "") + " mà bảng đích không có — bỏ qua.");
            }

            var srcKeys = srcInfo.Where(kv => kv.Value.KeyOrdinal > 0).Select(kv => kv.Key)
                                 .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!srcKeys.SetEquals(keys.Select(k => k.Name)))
            {
                p.Warnings.Add("Khóa chính bên nguồn khác bên đích — nếu nguồn có nhiều dòng trùng khóa, lần chạy sẽ dừng và báo lỗi.");
            }

            p.KeyIdx = Enumerable.Range(0, keys.Count).ToArray();
            p.CompareIdx = Enumerable.Range(keys.Count, p.Cols.Count - keys.Count)
                                     .Where(i => !p.Cols[i].IsIdentity)
                                     .ToArray();

            var label = _meta.GuessLabelColumn(p.Cols);
            p.LabelIdx = label == null ? -1 : p.Cols.IndexOf(label);

            p.Target = _module.Connection as SqlConnection
                       ?? throw new InvalidOperationException("Chép bảng chỉ hỗ trợ SQL Server");
            p.Source = scope.Connection as SqlConnection
                       ?? throw new InvalidOperationException("Chép bảng chỉ hỗ trợ SQL Server");

            if (checkSize)
            {
                var here = await StatsAsync(_module, table);
                var there = await StatsAsync(scope, srcTable);
                p.TargetApprox = here.Values.FirstOrDefault().Rows ?? -1;
                p.SourceApprox = there.Values.FirstOrDefault().Rows ?? -1;

                var max = Copy.MaxRowsPerTable;
                if (max > 0 && (p.TargetApprox > max || p.SourceApprox > max))
                {
                    throw new InvalidOperationException(
                        $"Bảng {table.Full} có khoảng {Math.Max(p.TargetApprox, p.SourceApprox):N0} dòng, vượt trần "
                        + $"Copy:MaxRowsPerTable = {max:N0}. Bảng cỡ này nên chép bằng backup/restore.");
                }
            }

            return p;
        }

        private void EnsureDifferent(ModuleDefinition source)
        {
            if (source.Key == _module.Current.Key)
            {
                throw new InvalidOperationException("Database nguồn và đích là một — chọn database nguồn khác");
            }
        }

        /// <summary>Collation and primary-key position of every column — what MetadataService does not keep.</summary>
        private static async Task<Dictionary<string, (string? Collation, int KeyOrdinal)>> ColumnInfoAsync(
            IModuleScope scope, TableInfo table)
        {
            const string sql = @"
SELECT c.name, c.collation_name, CAST(ISNULL(k.key_ordinal, 0) AS int) AS KeyOrdinal
FROM sys.columns c
LEFT JOIN (
    SELECT ic.column_id, ic.key_ordinal
    FROM sys.index_columns ic
    JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
    WHERE i.is_primary_key = 1 AND ic.object_id = OBJECT_ID(@obj)
) k ON k.column_id = c.column_id
WHERE c.object_id = OBJECT_ID(@obj)";

            var ps = new[] { new KeyValuePair<string, object?>("@obj", table.Quoted) };

            return await DbHelper.ExecAsync(scope.Connection, sql, 60, ps, async cmd =>
            {
                var result = new Dictionary<string, (string?, int)>(StringComparer.OrdinalIgnoreCase);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    result[reader.GetString(0)] = (reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt32(2));
                }
                return result;
            });
        }

        // ----------------------------------------------------------------- scan

        /// <summary>
        /// Walks the table in key order from <paramref name="cursor"/>, handing every difference to
        /// <paramref name="onChunk"/>, until the end or until <c>Copy:StepSeconds</c> have passed.
        /// Returns where to resume, or null at the end.
        ///
        /// A source page fixes the range (cursor, last key of the page]; the target is then read over
        /// exactly that range, itself a page at a time, so a target holding far more rows than the
        /// source never has to fit in memory. The last source page has no upper bound, which is what
        /// finds the target rows past the source's final key.
        ///
        /// Writing inside <paramref name="onChunk"/> is safe: everything it touches lies inside the
        /// range that has just been read, and the next read starts after it.
        /// </summary>
        private async Task<object?[]?> ScanAsync(Pair p, object?[]? cursor, Stopwatch watch,
            Func<Chunk, Task> onChunk, CancellationToken ct)
        {
            int n = Math.Max(1, Copy.ChunkSize);
            var last = cursor;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var sp = new ParamBag();
                var src = await ReadAsync(p.Source,
                    SelectSql(p, source: true, last == null ? null : After(p, true, last, sp), null, n), sp.List, ct,
                    types => p.SourceTypes ??= types);

                bool full = src.Count == n;
                var upper = full ? KeyOf(p, src[^1]) : null;

                var map = new Dictionary<string, object?[]>(StringComparer.Ordinal);
                foreach (var r in src)
                {
                    if (!map.TryAdd(Sig(p, r), r))
                    {
                        throw new InvalidOperationException(
                            $"Bên nguồn có nhiều dòng cùng khóa ({string.Join(", ", KeyOf(p, r).Select(Str))}) — không chép được bảng này");
                    }
                }

                var matched = new HashSet<string>(StringComparer.Ordinal);
                var tAfter = last;

                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    var tp = new ParamBag();
                    var tgt = await ReadAsync(p.Target,
                        SelectSql(p, source: false,
                            tAfter == null ? null : After(p, false, tAfter, tp),
                            upper == null ? null : UpTo(p, false, upper, tp), n),
                        tp.List, ct);

                    var chunk = new Chunk { TargetScanned = tgt.Count };
                    foreach (var t in tgt)
                    {
                        var sig = Sig(p, t);
                        if (map.TryGetValue(sig, out var s))
                        {
                            matched.Add(sig);
                            var changed = Diff(p, s, t);
                            if (changed.Count == 0) chunk.Same++;
                            else chunk.Updates.Add(new Changed(s, t, changed));
                        }
                        else
                        {
                            chunk.Deletes.Add(t);
                        }
                    }

                    if (tgt.Count > 0) await onChunk(chunk);

                    if (tgt.Count < n) break;
                    tAfter = KeyOf(p, tgt[^1]);
                }

                var ins = new Chunk { SourceScanned = src.Count };
                foreach (var r in src)
                {
                    if (!matched.Contains(Sig(p, r))) ins.Inserts.Add(r);
                }
                await onChunk(ins);

                if (!full) return null;

                last = upper;
                if (watch.Elapsed.TotalSeconds >= Copy.StepSeconds) return last;
            }
        }

        /// <summary>
        /// The rows the user ticked, looked up by key on both sides and classified the same way a full
        /// scan would. A row that no longer differs simply produces nothing.
        /// </summary>
        private async Task ScanKeysAsync(Pair p, List<Dictionary<string, string?>> keys, Func<Chunk, Task> onChunk,
            CancellationToken ct)
        {
            var parsed = keys.Select(k => p.KeyIdx.Select(i =>
            {
                var col = p.Cols[i];
                var hit = k.FirstOrDefault(kv => string.Equals(kv.Key, col.Name, StringComparison.OrdinalIgnoreCase));
                if (hit.Key == null) throw new InvalidOperationException($"Khóa gửi lên thiếu cột {col.Name}");
                return SqlValue.Write(col, hit.Value);
            }).ToArray()).ToList();

            int per = Math.Max(1, Math.Min(500, MaxParams / p.KeyIdx.Length));

            for (int i = 0; i < parsed.Count; i += per)
            {
                ct.ThrowIfCancellationRequested();
                var part = parsed.Skip(i).Take(per).ToList();

                var sp = new ParamBag();
                var src = await ReadAsync(p.Source, SelectSql(p, true, KeyIn(p, true, part, sp), null, 0),
                    sp.List, ct, types => p.SourceTypes ??= types);

                var tp = new ParamBag();
                var tgt = await ReadAsync(p.Target, SelectSql(p, false, KeyIn(p, false, part, tp), null, 0),
                    tp.List, ct);

                var srcMap = new Dictionary<string, object?[]>(StringComparer.Ordinal);
                foreach (var r in src)
                {
                    if (!srcMap.TryAdd(Sig(p, r), r))
                    {
                        throw new InvalidOperationException("Bên nguồn có nhiều dòng cùng khóa — không chép được bảng này");
                    }
                }

                var chunk = new Chunk { SourceScanned = src.Count, TargetScanned = tgt.Count };
                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (var t in tgt)
                {
                    var sig = Sig(p, t);
                    seen.Add(sig);
                    if (srcMap.TryGetValue(sig, out var s))
                    {
                        var changed = Diff(p, s, t);
                        if (changed.Count == 0) chunk.Same++;
                        else chunk.Updates.Add(new Changed(s, t, changed));
                    }
                    else
                    {
                        chunk.Deletes.Add(t);
                    }
                }
                foreach (var kv in srcMap)
                {
                    if (!seen.Contains(kv.Key)) chunk.Inserts.Add(kv.Value);
                }

                await onChunk(chunk);
            }
        }

        private string SelectSql(Pair p, bool source, string? after, string? upTo, int top)
        {
            var cols = source ? p.SrcCols : p.Cols;
            var keys = p.KeyIdx.Select(i => cols[i]).ToList();
            var where = new[] { after, upTo }.Where(x => x != null).ToList();

            return "SELECT " + string.Join(", ", cols.Select(Q))
                   + "\nFROM " + (source ? p.SourceTable : p.Table).Quoted
                   + (where.Count == 0 ? "" : "\nWHERE " + string.Join("\n  AND ", where))
                   + "\nORDER BY " + string.Join(", ", keys.Select(Q))
                   + (top > 0 ? "\nOFFSET 0 ROWS FETCH NEXT " + top + " ROWS ONLY" : "");
        }

        /// <summary>
        /// Rows strictly after <paramref name="key"/> in key order:
        /// k1 &gt; a OR (k1 = a AND (k2 &gt; b OR ...)). Led by k1 &gt;= a so the index can seek.
        /// </summary>
        private static string After(Pair p, bool source, object?[] key, ParamBag bag)
            => KeyCompare(p, source, key, bag, ">", ">", ">=");

        /// <summary>Rows up to and including <paramref name="key"/> in key order.</summary>
        private static string UpTo(Pair p, bool source, object?[] key, ParamBag bag)
            => KeyCompare(p, source, key, bag, "<", "<=", "<=");

        private static string KeyCompare(Pair p, bool source, object?[] key, ParamBag bag,
            string strict, string last, string lead)
        {
            var cols = source ? p.SrcCols : p.Cols;
            var keys = p.KeyIdx.Select(i => cols[i]).ToList();
            var names = keys.Select((c, j) => bag.Add(c, key[j])).ToList();

            string Build(int j) => j == keys.Count - 1
                ? Q(keys[j]) + " " + last + " " + names[j]
                : "(" + Q(keys[j]) + " " + strict + " " + names[j]
                  + " OR (" + Q(keys[j]) + " = " + names[j] + " AND " + Build(j + 1) + "))";

            return (keys.Count > 1 ? Q(keys[0]) + " " + lead + " " + names[0] + " AND " : "") + Build(0);
        }

        private static string KeyIn(Pair p, bool source, List<object?[]> keys, ParamBag bag)
        {
            var cols = source ? p.SrcCols : p.Cols;
            var kc = p.KeyIdx.Select(i => cols[i]).ToList();

            if (kc.Count == 1)
            {
                return Q(kc[0]) + " IN (" + string.Join(", ", keys.Select(k => bag.Add(kc[0], k[0]))) + ")";
            }

            return "(" + string.Join("\n   OR ", keys.Select(k =>
                "(" + string.Join(" AND ", kc.Select((c, j) => Q(c) + " = " + bag.Add(c, k[j]))) + ")")) + ")";
        }

        private static string KeyEquals(Pair p, object?[] row, GuardedBatch batch)
            => string.Join(" AND ", p.KeyIdx.Select(i => Q(p.Cols[i]) + " = " + batch.P(p.Cols[i], row[i])));

        private async Task<List<object?[]>> ReadAsync(SqlConnection conn, string sql, List<SqlParameter> ps,
            CancellationToken ct, Action<Type[]>? types = null)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = NoLock + sql;
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            cmd.Parameters.AddRange(ps.ToArray());

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            int n = reader.FieldCount;
            types?.Invoke(Enumerable.Range(0, n).Select(reader.GetFieldType).ToArray());

            // Raw values, not SqlValue.Read's strings: a copy has to write back exactly what it read,
            // and the string form drops the last digits of a datetime2.
            var rows = new List<object?[]>();
            while (await reader.ReadAsync(ct))
            {
                var row = new object?[n];
                for (int i = 0; i < n; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }

        private static async Task<IAsyncDisposable> OpenBothAsync(Pair p, CancellationToken ct)
        {
            bool t = false, s = false;
            if (p.Target.State != ConnectionState.Open) { await p.Target.OpenAsync(ct); t = true; }
            try
            {
                if (p.Source.State != ConnectionState.Open) { await p.Source.OpenAsync(ct); s = true; }
            }
            catch
            {
                if (t) await p.Target.CloseAsync();
                throw;
            }
            return new Closer(t ? p.Target : null, s ? p.Source : null);
        }

        private sealed class Closer : IAsyncDisposable
        {
            private readonly DbConnection? _a, _b;
            public Closer(DbConnection? a, DbConnection? b) { _a = a; _b = b; }

            public async ValueTask DisposeAsync()
            {
                if (_a != null) await _a.CloseAsync();
                if (_b != null) await _b.CloseAsync();
            }
        }

        // -------------------------------------------------------------- helpers

        private static string Q(ColumnInfo c) => "[" + c.Name.Replace("]", "]]") + "]";

        private static object?[] KeyOf(Pair p, object?[] row) => p.KeyIdx.Select(i => row[i]).ToArray();

        /// <summary>
        /// Identity of a row's key, compared the way SQL Server compares it: trailing spaces never
        /// count, and case does not either unless the column's collation says it does. Anything
        /// stricter reports the same row as missing on one side and extra on the other.
        /// </summary>
        private static string Sig(Pair p, object?[] row)
            => string.Join(Separator, p.KeyIdx.Select((i, j) => row[i] switch
            {
                null => "\0",
                string s => p.CaseInsensitive[j] ? s.TrimEnd().ToUpperInvariant() : s.TrimEnd(),
                DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
                byte[] b => Convert.ToHexString(b),
                var v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
            }));

        private static List<int> Diff(Pair p, object?[] src, object?[] tgt)
            => p.CompareIdx.Where(i => !Same(src[i], tgt[i])).ToList();

        /// <summary>
        /// Exact equality — unlike the field sync, case and trailing spaces here ARE differences:
        /// the point is for the copy to end up holding what the source holds.
        /// </summary>
        private static bool Same(object? a, object? b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a is byte[] x && b is byte[] y) return x.AsSpan().SequenceEqual(y);
            if (a.GetType() == b.GetType()) return a.Equals(b);

            // Same column, different type on each side (int here, bigint there): compare the values.
            return string.Equals(Convert.ToString(a, CultureInfo.InvariantCulture),
                Convert.ToString(b, CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        private static object?[]? ParseCursor(Pair p, List<string?>? cursor)
        {
            if (cursor == null) return null;
            if (cursor.Count != p.KeyIdx.Length)
            {
                throw new InvalidOperationException("Vị trí tiếp tục không khớp khóa chính của bảng — so sánh lại từ đầu");
            }

            // Parsed against the SOURCE columns: the cursor is where the source walk stopped.
            return p.KeyIdx.Select((i, j) => SqlValue.Write(p.SrcCols[i], cursor[j])).ToArray();
        }

        private static List<string?>? CursorOut(object?[]? key) => key?.Select(Str).ToList();

        private static Dictionary<string, string?> KeyDict(Pair p, object?[] row)
            => p.KeyIdx.ToDictionary(i => p.Cols[i].Name, i => Str(row[i]));

        private static string? LabelOf(Pair p, object?[] row) => p.LabelIdx < 0 ? null : Disp(row[p.LabelIdx]);

        private static Dictionary<string, string?> ValuesOf(Pair p, object?[] row)
            => p.Cols.Select((c, i) => (c.Name, i)).ToDictionary(x => x.Name, x => Disp(row[x.i]));

        /// <summary>The grid's string form — also what the audit log and the undo script are built from.</summary>
        private static string? Str(object? v)
            => v == null ? null : Convert.ToString(SqlValue.Read(v), CultureInfo.InvariantCulture);

        /// <summary>For showing only: a long text or a blob would make the comparison unreadable.</summary>
        private static string? Disp(object? v)
        {
            if (v is byte[] b)
            {
                return "0x" + Convert.ToHexString(b, 0, Math.Min(b.Length, 32))
                       + (b.Length > 32 ? $"… ({b.Length:N0} byte)" : "");
            }

            var s = Str(v);
            return s != null && s.Length > 300 ? s[..300] + "…" : s;
        }

        /// <summary>
        /// Parameter types set by hand where inference gets them wrong: a .NET string becomes
        /// nvarchar, which against a varchar key turns every lookup into a scan; a DateTime becomes
        /// datetime, which rounds away what a datetime2 holds.
        /// </summary>
        private static SqlParameter MakeParam(string name, ColumnInfo c, object? v)
        {
            var sp = new SqlParameter(name, v ?? DBNull.Value);
            SqlDbType? t = c.DataType.ToLowerInvariant() switch
            {
                "char" or "varchar" => SqlDbType.VarChar,
                "nchar" or "nvarchar" => SqlDbType.NVarChar,
                "text" => SqlDbType.Text,
                "ntext" => SqlDbType.NText,
                "date" => SqlDbType.Date,
                "datetime" => SqlDbType.DateTime,
                "datetime2" => SqlDbType.DateTime2,
                "smalldatetime" => SqlDbType.SmallDateTime,
                "datetimeoffset" => SqlDbType.DateTimeOffset,
                "time" => SqlDbType.Time,
                "image" => SqlDbType.Image,
                "sql_variant" => SqlDbType.Variant,
                _ => null
            };
            if (t != null) sp.SqlDbType = t.Value;
            return sp;
        }

        private sealed class ParamBag
        {
            public List<SqlParameter> List { get; } = new();

            public string Add(ColumnInfo c, object? v)
            {
                var name = "@q" + List.Count;
                List.Add(MakeParam(name, c, v));
                return name;
            }
        }

        /// <summary>
        /// Row-by-row UPDATE / DELETE statements sent many per round trip, each checked to have hit
        /// exactly one row. @@ROWCOUNT is read straight after each statement, so rows a trigger
        /// touches do not count against it the way ExecuteNonQuery's total would.
        /// </summary>
        private sealed class GuardedBatch
        {
            private readonly SqlConnection _conn;
            private readonly SqlTransaction _tran;
            private readonly int _timeout;
            private readonly StringBuilder _sql = new();
            private readonly List<SqlParameter> _ps = new();
            private int _count;

            public GuardedBatch(SqlConnection conn, SqlTransaction tran, int timeout)
            {
                _conn = conn;
                _tran = tran;
                _timeout = timeout;
            }

            public string P(ColumnInfo c, object? v)
            {
                var name = "@p" + _ps.Count;
                _ps.Add(MakeParam(name, c, v));
                return name;
            }

            /// <summary>Sends what is queued if the next statement's parameters would not fit.</summary>
            public Task FlushIfFullAsync(int nextParams, CancellationToken ct)
                => _ps.Count + nextParams > MaxParams || _count >= MaxStatements ? FlushAsync(ct) : Task.CompletedTask;

            public void Add(string statement)
            {
                _sql.Append(statement).Append(";\nIF @@ROWCOUNT <> 1 SET @bad = @bad + 1;\n");
                _count++;
            }

            public async Task FlushAsync(CancellationToken ct)
            {
                if (_count == 0) return;

                await using var cmd = _conn.CreateCommand();
                cmd.Transaction = _tran;
                cmd.CommandTimeout = _timeout;
                cmd.CommandText = "SET NOCOUNT ON;\nDECLARE @bad int = 0;\n" + _sql + "SELECT @bad;";
                cmd.Parameters.AddRange(_ps.ToArray());

                var bad = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));

                _sql.Clear();
                _ps.Clear();
                _count = 0;

                if (bad > 0)
                {
                    // The WHERE is the whole primary key, so this means the row moved under us (or the
                    // key is not what the metadata says). Either way nothing in this batch is trusted.
                    throw new InvalidOperationException(
                        $"{bad} câu lệnh không khớp đúng 1 dòng — lô này đã được hoàn tác");
                }
            }
        }

        private sealed record Changed(object?[] Src, object?[] Tgt, List<int> Columns);

        private sealed class Chunk
        {
            public List<object?[]> Inserts { get; } = new();
            public List<Changed> Updates { get; } = new();
            public List<object?[]> Deletes { get; } = new();
            public int Same { get; set; }
            public int SourceScanned { get; set; }
            public int TargetScanned { get; set; }
        }

        /// <summary>Both sides of one table, resolved and validated. Rows are arrays in <see cref="Cols"/> order.</summary>
        private sealed class Pair
        {
            public TableInfo Table = null!;
            public TableInfo SourceTable = null!;

            /// <summary>Target columns taken part in the copy: primary key first, in key order.</summary>
            public List<ColumnInfo> Cols = new();

            /// <summary>The source column for each entry of <see cref="Cols"/>, same index.</summary>
            public List<ColumnInfo> SrcCols = new();

            public int[] KeyIdx = Array.Empty<int>();

            /// <summary>Columns an UPDATE may set: not the key, not identity.</summary>
            public int[] CompareIdx = Array.Empty<int>();

            /// <summary>Per key column: compare case-insensitively, as its collation does.</summary>
            public List<bool> CaseInsensitive = new();

            public int LabelIdx = -1;

            /// <summary>CLR type of each source column, for the DataTable a bulk insert is fed from.</summary>
            public Type[]? SourceTypes;

            public long SourceApprox = -1;
            public long TargetApprox = -1;

            public List<string> Warnings = new();

            public SqlConnection Target = null!;
            public SqlConnection Source = null!;
        }
    }
}

using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using ToolPlus.Common;
using ToolPlus.Models.Explorer;
using ToolPlus.Models.ToolEntity;
using ToolPlus.Services.Interface;

namespace ToolPlus.Services.Explorer
{
    public class ExplorerService : IExplorerService
    {
        /// <summary>
        /// Every statement reads dirty: this tool runs against production databases and must never
        /// hold a lock that could stall the application using them.
        /// </summary>
        private const string NoLock = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;\n";

        private static readonly HashSet<string> CompareOps = new() { "=", "<>", ">", ">=", "<", "<=" };

        /// <summary>Keywords rejected by the SELECT console. INTO is included (SELECT … INTO creates a table).</summary>
        private static readonly Regex BannedWords = new(
            @"\b(insert|update|delete|drop|alter|truncate|create|merge|into|exec|execute|grant|revoke|deny|backup|restore|shutdown|reconfigure|openrowset|opendatasource|openquery|bulk|waitfor|dbcc|kill|sp_\w+|xp_\w+)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly IModuleContext _module;
        private readonly IMetadataService _meta;
        private readonly IAuditService _audit;
        private readonly ToolPlusOptions _opt;

        public ExplorerService(IModuleContext module, IMetadataService meta, IAuditService audit,
            IOptions<ToolPlusOptions> opt)
        {
            _module = module;
            _meta = meta;
            _audit = audit;
            _opt = opt.Value;
        }

        // ----------------------------------------------------------------- search

        public async Task<GridResult> SearchAsync(SearchRequest req)
        {
            var table = await ResolveOrThrowAsync(req.Table);
            var all = await _meta.GetColumnsAsync(table);

            var cols = all.Where(c => !c.IsSkipped && (req.IncludeLongText || !c.IsLong)).ToList();
            if (cols.Count == 0)
            {
                cols = all.Where(c => !c.IsSkipped).ToList();
            }
            if (cols.Count == 0)
            {
                throw new InvalidOperationException($"Table {table.Full} has no displayable columns");
            }

            var (where, ps) = BuildWhere(req, all);

            // OFFSET/FETCH requires an ORDER BY; prefer the primary key.
            var sort = _meta.ResolveColumn(cols, req.SortColumn)
                       ?? cols.FirstOrDefault(c => c.IsPrimaryKey)
                       ?? cols.First();

            int pageSize = Math.Clamp(req.PageSize, 1, _opt.MaxRows);
            int page = Math.Max(req.Page, 1);

            var sql = new StringBuilder()
                .Append("SELECT ").Append(string.Join(", ", cols.Select(c => "[" + c.Name + "]")))
                .Append("\nFROM ").Append(table.Quoted)
                .Append(where)
                .Append("\nORDER BY [").Append(sort.Name).Append(req.SortDesc ? "] DESC" : "] ASC")
                .Append("\nOFFSET ").Append((page - 1) * pageSize)
                .Append(" ROWS FETCH NEXT ").Append(pageSize).Append(" ROWS ONLY")
                .ToString();

            var watch = Stopwatch.StartNew();
            var (_, rows, truncated) = await DbHelper.ExecAsync(Conn, NoLock + sql, _opt.QueryTimeoutSeconds, ps,
                cmd => DbHelper.ReadAllAsync(cmd, pageSize));
            watch.Stop();

            return new GridResult
            {
                Columns = cols,
                Rows = rows,
                Sql = sql,
                ElapsedMs = watch.ElapsedMilliseconds,
                Truncated = truncated,
                Total = req.CountTotal ? await CountAsync(table, where, ps) : -1,
                HiddenColumns = all.Where(c => !cols.Contains(c)).Select(c => c.Name).ToList()
            };
        }

        private async Task<int> CountAsync(TableInfo table, string where, List<KeyValuePair<string, object?>> ps)
        {
            var sql = NoLock + "SELECT COUNT_BIG(1) FROM " + table.Quoted + where;
            try
            {
                // Counting a multi-million-row table is slow -> short timeout, and -1 on failure.
                return await DbHelper.ExecAsync(Conn, sql, Math.Min(_opt.QueryTimeoutSeconds, 20), ps, async cmd =>
                {
                    var raw = await cmd.ExecuteScalarAsync();
                    var n = Convert.ToInt64(raw ?? 0L);
                    return n > int.MaxValue ? int.MaxValue : (int)n;
                });
            }
            catch
            {
                return -1;
            }
        }

        private (string where, List<KeyValuePair<string, object?>> ps) BuildWhere(SearchRequest req, List<ColumnInfo> all)
        {
            var ps = new List<KeyValuePair<string, object?>>();
            var and = new List<string>();
            var collate = req.BoDau && !_opt.SearchCollation.IsEmpty() ? " COLLATE " + _opt.SearchCollation : "";

            if (!req.Keyword.IsEmpty())
            {
                var kw = req.Keyword!.Trim();
                var ors = new List<string>();

                var textCols = all.Where(c => c.IsText && !c.IsSkipped).ToList();
                if (textCols.Count > 0)
                {
                    ps.Add(new("@kw", "%" + SqlValue.EscapeLike(kw) + "%"));
                    ors.AddRange(textCols.Select(c => Castable(c) + collate + " LIKE @kw"));
                }

                // A numeric or guid keyword is also compared for equality on matching columns —
                // looking a record up by its id is the most common thing people do here.
                var numCols = all.Where(c => c.IsNumber).ToList();
                if (long.TryParse(kw, out var num) && numCols.Count > 0)
                {
                    ps.Add(new("@kwn", num));
                    ors.AddRange(numCols.Select(c => "[" + c.Name + "] = @kwn"));
                }

                var guidCols = all.Where(c => c.IsGuid).ToList();
                if (Guid.TryParse(kw, out var guid) && guidCols.Count > 0)
                {
                    ps.Add(new("@kwg", guid));
                    ors.AddRange(guidCols.Select(c => "[" + c.Name + "] = @kwg"));
                }

                // No searchable column -> return no rows rather than the whole table.
                and.Add(ors.Count > 0 ? "(" + string.Join("\n   OR ", ors) + ")" : "1 = 0");
            }

            int i = 0;
            foreach (var f in req.Filters)
            {
                var c = _meta.ResolveColumn(all, f.Column);
                if (c == null || c.IsSkipped) continue;

                var col = "[" + c.Name + "]";
                var op = (f.Op ?? "=").Trim().ToLowerInvariant();

                switch (op)
                {
                    case "null":
                        and.Add(col + " IS NULL");
                        break;

                    case "notnull":
                        and.Add(col + " IS NOT NULL");
                        break;

                    case "like":
                    case "startswith":
                    {
                        if (f.Value.IsEmpty()) break;
                        var name = "@f" + i++;
                        var v = SqlValue.EscapeLike(f.Value!);
                        ps.Add(new(name, op == "like" ? "%" + v + "%" : v + "%"));
                        and.Add(Castable(c) + collate + " LIKE " + name);
                        break;
                    }

                    case "in":
                    {
                        if (f.Value.IsEmpty()) break;
                        var names = new List<string>();
                        foreach (var part in f.Value!.Split(',', StringSplitOptions.RemoveEmptyEntries
                                                                 | StringSplitOptions.TrimEntries))
                        {
                            var name = "@f" + i++;
                            names.Add(name);
                            ps.Add(new(name, SqlValue.Write(c, part)));
                        }
                        if (names.Count > 0) and.Add(col + " IN (" + string.Join(", ", names) + ")");
                        break;
                    }

                    default:
                    {
                        if (!CompareOps.Contains(op)) break;
                        var name = "@f" + i++;
                        ps.Add(new(name, SqlValue.Write(c, f.Value)));
                        and.Add(col + " " + op + " " + name);
                        break;
                    }
                }
            }

            return (and.Count == 0 ? "" : "\nWHERE " + string.Join("\n  AND ", and), ps);
        }

        /// <summary>LIKE on a non-string column needs a CAST; nvarchar(max) is cast too, to keep it short.</summary>
        private static string Castable(ColumnInfo c)
            => c.IsText && c.MaxLength >= 0 ? "[" + c.Name + "]" : "CAST([" + c.Name + "] AS nvarchar(4000))";

        // ------------------------------------------------------------------- edit

        public async Task<List<ChangeLog>> UpdateAsync(UpdateRequest req, string username, string? ip)
        {
            var table = await ResolveOrThrowAsync(req.Table);
            if (table.IsView)
            {
                throw new InvalidOperationException($"{table.Full} is a view and cannot be edited");
            }

            var all = await _meta.GetColumnsAsync(table);
            var pks = all.Where(c => c.IsPrimaryKey).ToList();
            if (pks.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Table {table.Full} has no primary key — the tool cannot edit it safely");
            }

            // A missing key column means the WHERE clause could hit several rows -> reject up front.
            var keyPs = new List<KeyValuePair<string, object?>>();
            var whereParts = new List<string>();
            for (int i = 0; i < pks.Count; i++)
            {
                if (!TryGet(req.Key, pks[i].Name, out var raw))
                {
                    throw new InvalidOperationException($"Primary key value missing: {pks[i].Name}");
                }
                whereParts.Add("[" + pks[i].Name + "] = @k" + i);
                keyPs.Add(new("@k" + i, SqlValue.Write(pks[i], raw)));
            }
            var where = " WHERE " + string.Join(" AND ", whereParts);

            var setParts = new List<string>();
            var setPs = new List<KeyValuePair<string, object?>>();
            var targets = new List<ColumnInfo>();
            int j = 0;
            foreach (var kv in req.Changes)
            {
                var c = _meta.ResolveColumn(all, kv.Key)
                        ?? throw new InvalidOperationException($"Column {kv.Key} does not exist in {table.Full}");

                if (c.IsPrimaryKey) throw new InvalidOperationException($"Cannot edit primary key column {c.Name}");
                if (c.IsReadOnly) throw new InvalidOperationException($"Column {c.Name} is identity/computed and cannot be edited");
                if (c.IsSkipped) throw new InvalidOperationException($"Column {c.Name} (type {c.DataType}) cannot be edited through the tool");

                var value = SqlValue.Write(c, kv.Value);
                if (value == null && !c.IsNullable)
                {
                    throw new InvalidOperationException($"Column {c.Name} does not allow NULL");
                }

                setParts.Add("[" + c.Name + "] = @v" + j);
                setPs.Add(new("@v" + j, value));
                targets.Add(c);
                j++;
            }

            if (setParts.Count == 0) throw new InvalidOperationException("No changes to save");

            var conn = Conn;
            bool opened = false;
            if (conn.State != System.Data.ConnectionState.Open)
            {
                await conn.OpenAsync();
                opened = true;
            }

            try
            {
                // Snapshot before the change — both for the log and to prove the key hits one row.
                var before = await ReadBeforeAsync(conn, table, targets, where, keyPs);

                await using var tran = await conn.BeginTransactionAsync();
                var updateSql = "UPDATE " + table.Quoted + " SET " + string.Join(", ", setParts) + where;

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tran;
                    cmd.CommandText = updateSql;
                    cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
                    DbHelper.AddParams(cmd, setPs.Concat(keyPs));

                    var affected = await cmd.ExecuteNonQueryAsync();
                    if (affected != 1)
                    {
                        await tran.RollbackAsync();
                        throw new InvalidOperationException(
                            $"Statement affected {affected} row(s) (expected exactly 1) — rolled back, data unchanged");
                    }
                }

                await tran.CommitAsync();

                var changes = targets.Select((c, idx) => new ChangeLog
                {
                    Column = c.Name,
                    From = Stringify(before.GetValueOrDefault(c.Name)),
                    To = Stringify(setPs[idx].Value)
                }).ToList();

                await _audit.WriteAsync(new AuditLogEntry
                {
                    Type = "update",
                    User = username,
                    Ip = ip,
                    TableName = table.Full,
                    KeyJson = JsonConvert.SerializeObject(req.Key),
                    ChangesJson = JsonConvert.SerializeObject(changes),
                    Sql = updateSql
                });

                return changes;
            }
            finally
            {
                if (opened) await conn.CloseAsync();
            }
        }

        private async Task<Dictionary<string, object?>> ReadBeforeAsync(DbConnection conn, TableInfo table,
            List<ColumnInfo> targets, string where, List<KeyValuePair<string, object?>> keyPs)
        {
            var sql = "SELECT " + string.Join(", ", targets.Select(c => "[" + c.Name + "]"))
                      + " FROM " + table.Quoted + where;

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            DbHelper.AddParams(cmd, keyPs);

            var (_, rows, _) = await DbHelper.ReadAllAsync(cmd, 2);
            if (rows.Count == 0) throw new InvalidOperationException("Row not found by primary key (it may have been deleted)");
            if (rows.Count > 1) throw new InvalidOperationException("Primary key matched multiple rows — refusing to edit for safety");

            return rows[0];
        }

        // -------------------------------------------------------- console SELECT

        public async Task<GridResult> QueryAsync(string sql)
        {
            var clean = Validate(sql);

            var watch = Stopwatch.StartNew();
            var (names, rows, truncated) = await DbHelper.ExecAsync(Conn, NoLock + clean, _opt.QueryTimeoutSeconds,
                null, cmd => DbHelper.ReadAllAsync(cmd, _opt.MaxRows));
            watch.Stop();

            return new GridResult
            {
                // Free-form results cannot be mapped back to real columns -> read-only.
                Columns = names.Select(n => new ColumnInfo { Name = n, DataType = "", IsReadOnly = true }).ToList(),
                Rows = rows,
                Sql = clean,
                ElapsedMs = watch.ElapsedMilliseconds,
                Truncated = truncated,
                Total = rows.Count
            };
        }

        /// <summary>SELECT only: strip comments, then look for write keywords and an inner ";".</summary>
        private static string Validate(string sql)
        {
            var trimmed = (sql ?? "").Trim();
            if (trimmed.Length == 0) throw new InvalidOperationException("No statement entered");

            var clean = Regex.Replace(trimmed, @"--[^\n]*", " ");
            clean = Regex.Replace(clean, @"/\*.*?\*/", " ", RegexOptions.Singleline);

            var head = clean.TrimStart().TrimStart('(').TrimStart();
            if (!head.StartsWith("select", StringComparison.OrdinalIgnoreCase)
                && !head.StartsWith("with", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only SELECT and WITH statements are allowed");
            }

            if (clean.TrimEnd().TrimEnd(';').Contains(';'))
            {
                throw new InvalidOperationException("Only one statement at a time, semicolons are not allowed");
            }

            var banned = BannedWords.Match(clean);
            if (banned.Success)
            {
                throw new InvalidOperationException($"Statement contains a blocked keyword: {banned.Value}");
            }

            return trimmed.TrimEnd().TrimEnd(';');
        }

        // ----------------------------------------------------------------- shared

        private DbConnection Conn => _module.Connection;

        private async Task<TableInfo> ResolveOrThrowAsync(string name)
            => await _meta.ResolveTableAsync(name)
               ?? throw new InvalidOperationException($"Table or view \"{name}\" not found");

        private static bool TryGet(Dictionary<string, string?> dict, string key, out string? value)
        {
            foreach (var kv in dict)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = kv.Value;
                    return true;
                }
            }
            value = null;
            return false;
        }

        private static string? Stringify(object? v)
            => v switch
            {
                null or DBNull => null,
                DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd") : d.ToString("yyyy-MM-dd HH:mm:ss"),
                bool b => b ? "true" : "false",
                _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)
            };
    }
}

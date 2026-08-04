using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToolPlus.Common;
using ToolPlus.Models.Blueprints;
using ToolPlus.Models.Explorer;
using ToolPlus.Services.Interface;

namespace ToolPlus.Services.Blueprints
{
    /// <summary>
    /// Runs a blueprint as a SEQUENCE of per-level queries rather than translating it into one big JOIN.
    ///
    /// Why not a JOIN:
    ///   - Each node keeps its own table's primary key -> the edit button can target exactly one row.
    ///     A JOIN mixes the columns together and there is no telling which table a row belongs to.
    ///   - One-to-many relationships do not multiply rows (23 daily records × 3 contracts = 69 junk rows).
    ///   - A failing node fails alone; every other node still returns data.
    /// </summary>
    public class BlueprintRunner : IBlueprintRunner
    {
        /// <summary>Runs against live databases -> dirty reads, never hold a lock that could stall them.</summary>
        private const string NoLock = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;\n";

        /// <summary>SQL Server caps a statement at 2100 parameters; batch well below that.</summary>
        private const int KeyChunk = 200;

        private const int MaxParentKeys = 2000;
        private const int MaxDepth = 6;
        private const int MaxNodes = 12;

        private static readonly HashSet<string> CompareOps = new() { "=", "<>", ">", ">=", "<", "<=" };

        private static readonly HashSet<string> DateTypes = new(StringComparer.OrdinalIgnoreCase)
            { "date", "datetime", "datetime2", "smalldatetime", "datetimeoffset" };

        private static readonly Regex Token = BlueprintGraph.ParamToken;

        private readonly IModuleContext _module;
        private readonly IMetadataService _meta;
        private readonly ToolPlusOptions _opt;

        public BlueprintRunner(IModuleContext module, IMetadataService meta, IOptions<ToolPlusOptions> opt)
        {
            _module = module;
            _meta = meta;
            _opt = opt.Value;
        }

        public async Task<BlueprintRunResult> RunAsync(BlueprintGraph graph, Dictionary<string, string?> values, bool allowEdit)
        {
            var result = new BlueprintRunResult();
            var watch = Stopwatch.StartNew();

            if (graph.Nodes.Count == 0) throw new InvalidOperationException("Blueprint has no tables");
            if (graph.Nodes.Count > MaxNodes) throw new InvalidOperationException($"Blueprint exceeds the {MaxNodes}-table limit");

            CheckRequiredParams(graph, values);

            var order = Order(graph, result.Warnings);
            var done = new Dictionary<string, NodeResult>();

            foreach (var node in order)
            {
                var nr = new NodeResult { NodeId = node.Id, Table = node.Table, Alias = node.Alias };
                result.Nodes.Add(nr);

                try
                {
                    var parentEdge = graph.Edges.FirstOrDefault(e => e.ToNode == node.Id);
                    if (parentEdge != null && !parentEdge.IsParam)
                    {
                        nr.ParentNodeId = parentEdge.FromNode;
                        nr.LinkFromColumn = parentEdge.FromColumn;
                        nr.LinkToColumn = parentEdge.ToColumn;
                    }
                    else if (parentEdge == null && node.Filters.Count == 0)
                    {
                        // A node with its own filters needs no inbound link — a {param} token inside a
                        // filter is the only way for one table to take more than one parameter.
                        result.Warnings.Add($"\"{Label(node)}\" has no inbound link — returning only the first {Limit(node)} rows.");
                    }

                    await RunNodeAsync(graph, node, parentEdge, values, done, nr, allowEdit, result.Warnings);
                }
                catch (Exception ex)
                {
                    nr.Error = ex is InvalidOperationException ? ex.Message : ex.GetBaseException().Message;
                }

                done[node.Id] = nr;
            }

            watch.Stop();
            result.ElapsedMs = watch.ElapsedMilliseconds;
            return result;
        }

        // ------------------------------------------------------------- run order

        /// <summary>
        /// Orders the nodes so a parent always runs before its children. Whatever is left when no
        /// further progress is possible sits in a cycle — skip it and warn.
        /// </summary>
        private static List<GraphNode> Order(BlueprintGraph graph, List<string> warnings)
        {
            var remain = new List<GraphNode>(graph.Nodes);
            var ordered = new List<GraphNode>();
            var placed = new HashSet<string>();

            while (remain.Count > 0)
            {
                var ready = remain.Where(n =>
                {
                    var edge = graph.Edges.FirstOrDefault(e => e.ToNode == n.Id);
                    return edge == null || edge.IsParam || placed.Contains(edge.FromNode!);
                }).ToList();

                if (ready.Count == 0)
                {
                    warnings.Add("Cycle detected: " + remain.Count + " table(s) skipped: "
                                 + string.Join(", ", remain.Select(n => n.Alias.IsEmpty() ? n.Table : n.Alias)));
                    break;
                }

                foreach (var n in ready)
                {
                    ordered.Add(n);
                    placed.Add(n.Id);
                    remain.Remove(n);
                }
            }

            // Every extra level multiplies the number of queries, so warn once it gets deep.
            var depth = new Dictionary<string, int>();
            foreach (var n in ordered)
            {
                var edge = graph.Edges.FirstOrDefault(e => e.ToNode == n.Id);
                depth[n.Id] = edge == null || edge.IsParam ? 1 : depth[edge.FromNode!] + 1;
            }
            if (depth.Values.Any(d => d > MaxDepth))
            {
                warnings.Add($"Blueprint is deeper than {MaxDepth} levels; the deeper parts may be slow.");
            }

            return ordered;
        }

        // ----------------------------------------------------------- one node's run

        private async Task RunNodeAsync(BlueprintGraph graph, GraphNode node, GraphEdge? parentEdge,
            Dictionary<string, string?> values, Dictionary<string, NodeResult> done, NodeResult nr,
            bool allowEdit, List<string> warnings)
        {
            var table = await _meta.ResolveTableAsync(node.Table)
                        ?? throw new InvalidOperationException($"Table \"{node.Table}\" no longer exists in the database");

            var all = await _meta.GetColumnsAsync(table);
            nr.Table = table.Full;
            nr.IsView = table.IsView;

            var pks = all.Where(c => c.IsPrimaryKey).ToList();
            nr.CanEdit = allowEdit && !table.IsView && pks.Count > 0;

            // Columns fetched = primary key (to stay editable) + the ticked ones + the linked ones.
            var wanted = new List<ColumnInfo>();
            void Want(ColumnInfo? c)
            {
                if (c != null && !c.IsSkipped && !wanted.Contains(c)) wanted.Add(c);
            }

            pks.ForEach(Want);
            foreach (var name in node.Columns)
            {
                var c = _meta.ResolveColumn(all, name);
                if (c == null) { warnings.Add($"\"{Label(node)}\": column {name} no longer exists, skipped."); continue; }
                Want(c);
            }
            if (parentEdge != null) Want(_meta.ResolveColumn(all, parentEdge.ToColumn));
            foreach (var e in graph.Edges.Where(e => e.FromNode == node.Id)) Want(_meta.ResolveColumn(all, e.FromColumn));

            if (wanted.Count == 0) Want(all.FirstOrDefault(c => !c.IsSkipped));
            if (wanted.Count == 0) throw new InvalidOperationException("Table has no displayable columns");

            nr.Columns = wanted;
            nr.Shown = node.Columns.Where(n => wanted.Any(c => c.Name == n)).ToList();
            if (nr.Shown.Count == 0) nr.Shown = wanted.Select(c => c.Name).ToList();

            // ---- filters
            var ps = new List<KeyValuePair<string, object?>>();
            var and = new List<string>();
            int pi = 0;

            AddFilters(node, all, values, graph, and, ps, ref pi, warnings);

            List<object?>? parentKeys = null;
            if (parentEdge != null && parentEdge.IsParam)
            {
                AddParamCondition(graph, node, parentEdge, all, values, and, ps, ref pi, warnings);
            }
            else if (parentEdge != null)
            {
                var parent = done.GetValueOrDefault(parentEdge.FromNode!);
                if (parent == null || parent.Rows.Count == 0)
                {
                    nr.Sql = "-- parent table returned no rows, skipping query";
                    return;
                }

                parentKeys = parent.Rows
                    .Select(r => r.GetValueOrDefault(parentEdge.FromColumn!))
                    .Where(v => v != null)
                    .Distinct()
                    .ToList();

                if (parentKeys.Count == 0)
                {
                    nr.Sql = "-- link column in parent is all NULL, skipping query";
                    return;
                }
                if (parentKeys.Count > MaxParentKeys)
                {
                    warnings.Add($"\"{Label(node)}\": parent has {parentKeys.Count} values; only the first {MaxParentKeys} will be used.");
                    parentKeys = parentKeys.Take(MaxParentKeys).ToList();
                }
            }

            // ---- sort + row cap
            var sort = _meta.ResolveColumn(wanted, node.SortColumn)
                       ?? wanted.FirstOrDefault(c => c.IsPrimaryKey)
                       ?? wanted.First();

            int limit = Limit(node);
            var select = "SELECT " + string.Join(", ", wanted.Select(c => "[" + c.Name + "]"))
                                   + "\nFROM " + table.Quoted;
            var tail = "\nORDER BY [" + sort.Name + (node.SortDesc ? "] DESC" : "] ASC")
                       + "\nOFFSET 0 ROWS FETCH NEXT " + limit + " ROWS ONLY";

            var watch = Stopwatch.StartNew();

            if (parentKeys == null)
            {
                var sql = select + Where(and) + tail;
                nr.Sql = sql;
                var (_, rows, cut) = await DbHelper.ExecAsync(Conn, NoLock + sql, _opt.QueryTimeoutSeconds, ps,
                    cmd => DbHelper.ReadAllAsync(cmd, limit));
                nr.Rows = rows;
                nr.Truncated = cut;
            }
            else
            {
                // Batch the parent keys, accumulating rows until the cap is reached.
                var linkCol = _meta.ResolveColumn(all, parentEdge!.ToColumn)!;
                var merged = new List<Dictionary<string, object?>>();
                string shownSql = "";

                for (int i = 0; i < parentKeys.Count && merged.Count < limit; i += KeyChunk)
                {
                    var chunk = parentKeys.Skip(i).Take(KeyChunk).ToList();
                    var names = new List<string>();
                    var chunkPs = new List<KeyValuePair<string, object?>>(ps);

                    for (int j = 0; j < chunk.Count; j++)
                    {
                        var name = "@lk" + j;
                        names.Add(name);
                        chunkPs.Add(new(name, Coerce(linkCol, chunk[j])));
                    }

                    var conds = new List<string>(and) { "[" + linkCol.Name + "] IN (" + string.Join(", ", names) + ")" };
                    var sql = select + Where(conds) + tail;
                    if (shownSql.Length == 0) shownSql = sql;

                    var (_, rows, cut) = await DbHelper.ExecAsync(Conn, NoLock + sql, _opt.QueryTimeoutSeconds, chunkPs,
                        cmd => DbHelper.ReadAllAsync(cmd, limit - merged.Count));
                    merged.AddRange(rows);
                    if (cut) nr.Truncated = true;
                }

                nr.Sql = shownSql;
                nr.Rows = merged;
                if (merged.Count >= limit) nr.Truncated = true;
            }

            watch.Stop();
            nr.ElapsedMs = watch.ElapsedMilliseconds;
        }

        // ----------------------------------------------------------------- filters

        private void AddParamCondition(BlueprintGraph graph, GraphNode node, GraphEdge edge, List<ColumnInfo> all,
            Dictionary<string, string?> values, List<string> and, List<KeyValuePair<string, object?>> ps, ref int pi,
            List<string> warnings)
        {
            var col = _meta.ResolveColumn(all, edge.ToColumn)
                      ?? throw new InvalidOperationException($"Column {edge.ToColumn} for the parameter link no longer exists");

            var p = graph.Params.FirstOrDefault(x => x.Key == edge.FromParam)
                    ?? throw new InvalidOperationException($"Blueprint has no parameter \"{edge.FromParam}\"");

            values.TryGetValue(p.Key, out var raw);

            if (raw.IsEmpty())
            {
                if (p.Required) throw new InvalidOperationException($"Required parameter \"{ParamLabel(p)}\" was not provided");

                // Optional and left blank -> drop the condition. This is the ONLY link into this
                // table, so dropping it leaves no filter at all; say so, or the user believes they
                // are looking at filtered results when it is really just the first rows of the table.
                warnings.Add($"\"{Label(node)}\": \"{ParamLabel(p)}\" was not provided so {col.Name} is unfiltered — returning only the first {Limit(node)} rows.");
                return;
            }

            // A month parameter linked to a date column has to become a range, not an equality test.
            if (p.Type == "month" && DateTypes.Contains(col.DataType))
            {
                var (from, to) = MonthRange(raw!);
                var a = "@p" + pi++;
                var b = "@p" + pi++;
                and.Add("[" + col.Name + "] >= " + a + " AND [" + col.Name + "] < " + b);
                ps.Add(new(a, from));
                ps.Add(new(b, to));
                return;
            }

            var name = "@p" + pi++;
            and.Add("[" + col.Name + "] = " + name);
            ps.Add(new(name, SqlValue.Write(col, raw)));
        }

        private void AddFilters(GraphNode node, List<ColumnInfo> all, Dictionary<string, string?> values,
            BlueprintGraph graph, List<string> and, List<KeyValuePair<string, object?>> ps, ref int pi,
            List<string> warnings)
        {
            foreach (var f in node.Filters)
            {
                var c = _meta.ResolveColumn(all, f.Column);
                if (c == null || c.IsSkipped) continue;

                var col = "[" + c.Name + "]";
                var op = (f.Op ?? "=").Trim().ToLowerInvariant();
                var raw = ResolveTokens(f.Value, graph, values);

                // A filter holding a {param} whose value was left blank -> drop it. Say so, or the
                // user believes the results are filtered when they are seeing the whole table.
                if (raw.IsEmpty() && !f.Value.IsEmpty() && !NoValueOp(op))
                {
                    warnings.Add($"\"{Label(node)}\": value for {f.Value} was not provided, condition on {f.Column} dropped.");
                    continue;
                }

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
                        if (raw.IsEmpty()) break;
                        var name = "@f" + pi++;
                        var v = SqlValue.EscapeLike(raw!);
                        ps.Add(new(name, op == "like" ? "%" + v + "%" : v + "%"));
                        var expr = c.IsText && c.MaxLength >= 0 ? col : "CAST(" + col + " AS nvarchar(4000))";
                        var coll = _opt.SearchCollation.IsEmpty() ? "" : " COLLATE " + _opt.SearchCollation;
                        and.Add(expr + coll + " LIKE " + name);
                        break;
                    }

                    case "in":
                    {
                        if (raw.IsEmpty()) break;
                        var names = new List<string>();
                        foreach (var part in raw!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            var name = "@f" + pi++;
                            names.Add(name);
                            ps.Add(new(name, SqlValue.Write(c, part)));
                        }
                        if (names.Count > 0) and.Add(col + " IN (" + string.Join(", ", names) + ")");
                        break;
                    }

                    default:
                    {
                        if (!CompareOps.Contains(op) || raw.IsEmpty()) break;
                        var name = "@f" + pi++;
                        ps.Add(new(name, SqlValue.Write(c, raw)));
                        and.Add(col + " " + op + " " + name);
                        break;
                    }
                }
            }
        }

        private static bool NoValueOp(string op) => op is "null" or "notnull";

        /// <summary>
        /// Expands {paramName} inside a filter value. Two suffixes open a half-open range:
        ///   MONTH parameter -> {p:tu} = first of the month  · {p:den} = first of the NEXT month
        ///   DATE  parameter -> {p:tu} = 00:00 that day      · {p:den} = 00:00 the NEXT day
        /// That is what lets a datetime column use the pair "&gt;= {p:tu}" and "&lt; {p:den}" without
        /// losing rows that carry a time of day on the last date.
        /// </summary>
        private static string? ResolveTokens(string? value, BlueprintGraph graph, Dictionary<string, string?> values)
        {
            if (value.IsEmpty() || value!.IndexOf('{') < 0) return value;

            return Token.Replace(value, m =>
            {
                var key = m.Groups[1].Value;
                var mod = m.Groups[2].Value;
                values.TryGetValue(key, out var raw);
                if (raw.IsEmpty()) return "";

                if (mod is "tu" or "den")
                {
                    var p = graph.Params.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                    var (from, to) = p != null && p.Type == "date" ? DayRange(raw!) : MonthRange(raw!);
                    return (mod == "tu" ? from : to).ToString("yyyy-MM-dd");
                }
                return raw!;
            });
        }

        private static void CheckRequiredParams(BlueprintGraph graph, Dictionary<string, string?> values)
        {
            // Only parameters actually in use are demanded — one declared but unused is not asked for.
            var used = graph.UsedParamKeys();

            foreach (var p in graph.Params.Where(p => p.Required && used.Contains(p.Key)))
            {
                values.TryGetValue(p.Key, out var raw);
                if (raw.IsEmpty()) throw new InvalidOperationException($"Required parameter \"{ParamLabel(p)}\" was not provided");
            }
        }

        // ----------------------------------------------------------------- helpers

        private System.Data.Common.DbConnection Conn => _module.Connection;

        private static string Where(List<string> and)
            => and.Count == 0 ? "" : "\nWHERE " + string.Join("\n  AND ", and);

        private int Limit(GraphNode node) => Math.Clamp(node.Limit <= 0 ? 50 : node.Limit, 1, _opt.MaxRows);

        private static string Label(GraphNode n) => n.Alias.IsEmpty() ? n.Table : n.Alias;

        private static string ParamLabel(GraphParam p) => p.Label.IsEmpty() ? p.Key : p.Label;

        /// <summary>
        /// Values taken from a parent node have already been through SqlValue.Read, so dates and guids
        /// are strings by now. Convert them back to the target column's type, otherwise SQL Server has
        /// to convert on every row.
        /// </summary>
        private static object? Coerce(ColumnInfo col, object? value)
            => value == null ? null : SqlValue.Write(col, Convert.ToString(value, CultureInfo.InvariantCulture));

        private static (DateTime from, DateTime to) MonthRange(string raw)
        {
            var s = raw.Trim();
            DateTime first;

            if (DateTime.TryParseExact(s, new[] { "yyyy-MM", "MM/yyyy", "yyyy/MM" },
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var m))
            {
                first = new DateTime(m.Year, m.Month, 1);
            }
            else if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                first = new DateTime(d.Year, d.Month, 1);
            }
            else
            {
                throw new InvalidOperationException($"\"{raw}\" is not a valid month (expected format: 2026-07)");
            }

            return (first, first.AddMonths(1));
        }

        private static (DateTime from, DateTime to) DayRange(string raw)
        {
            var s = raw.Trim();

            if (!DateTime.TryParseExact(s, new[] { "yyyy-MM-dd", "dd/MM/yyyy", "dd-MM-yyyy" },
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                && !DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
            {
                throw new InvalidOperationException($"\"{raw}\" is not a valid date (expected format: 2026-07-31)");
            }

            return (d.Date, d.Date.AddDays(1));
        }
    }
}

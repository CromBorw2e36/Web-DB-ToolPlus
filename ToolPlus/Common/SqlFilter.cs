using ToolPlus.Models.Explorer;

namespace ToolPlus.Common
{
    /// <summary>
    /// Turns one client-supplied condition into a WHERE fragment and its parameters.
    ///
    /// The column must already have been resolved through MetadataService — that is the whitelist
    /// layer and this class trusts it. Everything else the client sent (operator, value) either has
    /// to match a fixed list or travels as a parameter, so nothing from a browser reaches the SQL
    /// text itself.
    ///
    /// ExplorerService and BlueprintRunner still carry their own version of this loop: theirs are
    /// wrapped in behaviour of their own (a keyword searched across every column, {param} token
    /// expansion). They could be migrated onto this once that behaviour is factored out too.
    /// </summary>
    public static class SqlFilter
    {
        private static readonly HashSet<string> CompareOps = new() { "=", "<>", ">", ">=", "<", "<=" };

        /// <summary>An operator that carries no value, so a blank value is not a reason to drop it.</summary>
        public static bool IsValueless(string? op)
        {
            var o = (op ?? "").Trim().ToLowerInvariant();
            return o is "null" or "notnull";
        }

        /// <summary>
        /// Appends the condition to <paramref name="and"/>. An operator that is not recognised, or a
        /// value that is missing where one is required, adds nothing — the caller decides whether a
        /// dropped condition is worth a warning.
        /// </summary>
        public static void Add(List<string> and, List<KeyValuePair<string, object?>> ps, ref int index,
            ColumnInfo c, string? op, string? value, string collation)
        {
            var col = "[" + c.Name + "]";
            var o = (op ?? "=").Trim().ToLowerInvariant();
            var collate = collation.IsEmpty() ? "" : " COLLATE " + collation;

            switch (o)
            {
                case "null":
                    and.Add(col + " IS NULL");
                    return;

                case "notnull":
                    and.Add(col + " IS NOT NULL");
                    return;

                case "like":
                case "startswith":
                {
                    if (value.IsEmpty()) return;
                    var name = "@g" + index++;
                    var v = SqlValue.EscapeLike(value!);
                    ps.Add(new(name, o == "like" ? "%" + v + "%" : v + "%"));

                    // LIKE on a non-string column needs a CAST; nvarchar(max) is cast too, to keep it short.
                    var expr = c.IsText && c.MaxLength >= 0 ? col : "CAST(" + col + " AS nvarchar(4000))";
                    and.Add(expr + collate + " LIKE " + name);
                    return;
                }

                case "in":
                {
                    if (value.IsEmpty()) return;
                    var names = new List<string>();
                    foreach (var part in value!.Split(',', StringSplitOptions.RemoveEmptyEntries
                                                          | StringSplitOptions.TrimEntries))
                    {
                        var name = "@g" + index++;
                        names.Add(name);
                        ps.Add(new(name, SqlValue.Write(c, part)));
                    }
                    if (names.Count > 0) and.Add(col + " IN (" + string.Join(", ", names) + ")");
                    return;
                }

                default:
                {
                    if (!CompareOps.Contains(o) || value.IsEmpty()) return;
                    var name = "@g" + index++;
                    ps.Add(new(name, SqlValue.Write(c, value)));
                    and.Add(col + " " + o + " " + name);
                    return;
                }
            }
        }
    }
}

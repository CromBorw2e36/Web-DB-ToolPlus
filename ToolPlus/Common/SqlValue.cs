using System.Globalization;
using ToolPlus.Models.Explorer;

namespace ToolPlus.Common
{
    /// <summary>
    /// Converts values both ways between the database and the string shown in the grid. The string
    /// form must round-trip: editing one cell and saving has to parse every other value back to
    /// exactly what it was.
    /// </summary>
    public static class SqlValue
    {
        private static readonly string[] DateFormats =
        {
            "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd",
            "yyyy-MM-ddTHH:mm:ss.fff", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm",
            "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "dd/MM/yyyy"
        };

        /// <summary>DB -&gt; JSON. Numbers and booleans keep their type so JS compares and sorts them correctly.</summary>
        public static object? Read(object? v)
        {
            switch (v)
            {
                case null:
                case DBNull:
                    return null;

                case DateTime d:
                    if (d.TimeOfDay == TimeSpan.Zero) return d.ToString("yyyy-MM-dd");
                    return d.Millisecond == 0
                        ? d.ToString("yyyy-MM-dd HH:mm:ss")
                        : d.ToString("yyyy-MM-dd HH:mm:ss.fff");

                case DateTimeOffset dto:
                    return dto.ToString("yyyy-MM-dd HH:mm:ss zzz");

                case TimeSpan ts:
                    return ts.ToString();

                case Guid g:
                    return g.ToString();

                case byte[] b:
                    return "0x" + Convert.ToHexString(b);

                case bool or byte or short or int or long or float or double or decimal:
                    return v;

                default:
                    return v.ToString();
            }
        }

        /// <summary>
        /// Grid string -&gt; SQL parameter value. Throws with a message written for the end user, so
        /// the UI can show it as-is instead of an opaque ADO.NET error.
        /// </summary>
        public static object? Write(ColumnInfo col, string? raw)
        {
            if (raw == null) return null;

            var t = col.DataType.ToLowerInvariant();
            if (t is "char" or "nchar" or "varchar" or "nvarchar" or "text" or "ntext" or "xml")
            {
                return raw;
            }

            var s = raw.Trim();
            if (s.Length == 0) return null;

            try
            {
                switch (t)
                {
                    case "bit":
                        if (s is "1") return true;
                        if (s is "0") return false;
                        return bool.Parse(s);

                    case "tinyint":
                    case "smallint":
                    case "int":
                        return int.Parse(s, CultureInfo.InvariantCulture);

                    case "bigint":
                        return long.Parse(s, CultureInfo.InvariantCulture);

                    case "decimal":
                    case "numeric":
                    case "money":
                    case "smallmoney":
                        return decimal.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture);

                    case "float":
                        return double.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture);

                    case "real":
                        return float.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture);

                    case "uniqueidentifier":
                        return Guid.Parse(s);

                    case "date":
                    case "datetime":
                    case "datetime2":
                    case "smalldatetime":
                        return ParseDate(s);

                    case "time":
                        return TimeSpan.Parse(s, CultureInfo.InvariantCulture);

                    case "datetimeoffset":
                        return DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);

                    default:
                        return s;
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                throw new InvalidOperationException(
                    $"Value \"{raw}\" is not a valid {col.DataType} for column {col.Name}");
            }
        }

        private static DateTime ParseDate(string s)
        {
            if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var d))
            {
                return d;
            }

            return DateTime.Parse(s, CultureInfo.InvariantCulture);
        }

        /// <summary>Neutralises LIKE wildcards so the search matches exactly what the user typed.</summary>
        public static string EscapeLike(string s)
            => s.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
    }
}

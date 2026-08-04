using System.Data;
using System.Data.Common;

namespace ToolPlus.Common
{
    public static class DbHelper
    {
        /// <summary>
        /// Opens the connection if it is closed, runs <paramref name="body"/>, then restores the
        /// original state — whoever owns the connection may already have it open, and closing that
        /// out from under them would break the caller.
        /// </summary>
        public static async Task<T> ExecAsync<T>(DbConnection conn, string sql, int timeoutSeconds,
            IEnumerable<KeyValuePair<string, object?>>? ps, Func<DbCommand, Task<T>> body)
        {
            bool opened = false;
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync();
                opened = true;
            }

            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = timeoutSeconds;
                AddParams(cmd, ps);
                return await body(cmd);
            }
            finally
            {
                if (opened) await conn.CloseAsync();
            }
        }

        public static void AddParams(DbCommand cmd, IEnumerable<KeyValuePair<string, object?>>? ps)
        {
            if (ps == null) return;
            foreach (var p in ps)
            {
                var dp = cmd.CreateParameter();
                dp.ParameterName = p.Key;
                dp.Value = p.Value ?? DBNull.Value;
                cmd.Parameters.Add(dp);
            }
        }

        /// <summary>Reads the reader into a list of dictionaries, stopping at <paramref name="maxRows"/>.</summary>
        public static async Task<(List<string> names, List<Dictionary<string, object?>> rows, bool truncated)>
            ReadAllAsync(DbCommand cmd, int maxRows)
        {
            await using var reader = await cmd.ExecuteReaderAsync();

            var names = new List<string>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var n = reader.GetName(i);
                // Unnamed (expression) or duplicate columns get a generated name, so the JS side
                // does not silently overwrite one with the other.
                if (n.IsEmpty()) n = "col" + (i + 1);
                while (names.Contains(n)) n += "_";
                names.Add(n);
            }

            var rows = new List<Dictionary<string, object?>>();
            bool truncated = false;
            while (await reader.ReadAsync())
            {
                if (rows.Count >= maxRows) { truncated = true; break; }

                var row = new Dictionary<string, object?>(names.Count);
                for (int i = 0; i < names.Count; i++)
                {
                    row[names[i]] = await reader.IsDBNullAsync(i) ? null : SqlValue.Read(reader.GetValue(i));
                }
                rows.Add(row);
            }

            return (names, rows, truncated);
        }
    }
}

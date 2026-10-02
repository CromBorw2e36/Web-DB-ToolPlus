using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ToolPlus.Common;
using ToolPlus.Models.Explorer;
using ToolPlus.Services.Interface;

namespace ToolPlus.Services.Explorer
{
    /// <summary>
    /// Reads database structure from sys.*, which is what lets Explorer work against any table with
    /// no scaffolded entity. It is also the whitelist layer: every table and column name that ends up
    /// in SQL must be resolved through here first, leaving no route for SQL injection.
    /// </summary>
    public class MetadataService : IMetadataService
    {
        private const string CacheTables = "tp-tables";
        private const string CacheColumns = "tp-cols-";
        private const string CacheForeignKeys = "tp-fks";

        private static readonly MemoryCacheEntryOptions CacheOptions = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(TimeSpan.FromMinutes(10));

        /// <summary>Types that cannot be rendered in a grid -&gt; never SELECTed.</summary>
        private static readonly HashSet<string> SkipTypes = new(StringComparer.OrdinalIgnoreCase)
            { "varbinary", "binary", "image", "geography", "geometry", "hierarchyid", "sql_variant" };

        private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
            { "char", "nchar", "varchar", "nvarchar", "text", "ntext", "xml" };

        private static readonly HashSet<string> IntTypes = new(StringComparer.OrdinalIgnoreCase)
            { "tinyint", "smallint", "int", "bigint" };

        private readonly IModuleScope _module;
        private readonly MemoryCacheManager _cache;

        /// <summary>
        /// Takes an <see cref="IModuleScope"/> rather than the request's own context so the same
        /// whitelist can be applied to a SECOND database — the source side of a field sync builds
        /// one of these over a SecondaryModuleScope. Cache keys are already module-prefixed, so the
        /// two never see each other's structure.
        /// </summary>
        public MetadataService(IModuleScope module, MemoryCacheManager cache)
        {
            _module = module;
            _cache = cache;
        }

        public async Task<List<TableInfo>> GetTablesAsync()
        {
            var cacheKey = _module.CacheKey(CacheTables);
            if (_cache.TryGetValue(cacheKey, out List<TableInfo>? cached) && cached != null)
            {
                return cached;
            }

            const string sql = @"
SELECT s.name AS SchemaName, o.name AS ObjectName, o.type AS ObjectType
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0
ORDER BY o.name";

            var list = await DbHelper.ExecAsync(_module.Connection, sql, 60, null, async cmd =>
            {
                var result = new List<TableInfo>();
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    result.Add(new TableInfo
                    {
                        Schema = reader.GetString(0),
                        Name = reader.GetString(1),
                        IsView = reader.GetString(2).Trim() == "V"
                    });
                }
                return result;
            });

            _cache.Set(cacheKey, list, CacheOptions);
            return list;
        }

        public async Task<TableInfo?> ResolveTableAsync(string? name)
        {
            if (name.IsEmpty()) return null;

            var tables = await GetTablesAsync();
            var t = tables.FirstOrDefault(x => string.Equals(x.Full, name, StringComparison.OrdinalIgnoreCase));

            // A bare name (no schema) is accepted as long as it is unambiguous.
            return t ?? tables.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<List<ColumnInfo>> GetColumnsAsync(TableInfo table)
        {
            var key = _module.CacheKey(CacheColumns + table.Full.ToLowerInvariant());
            if (_cache.TryGetValue(key, out List<ColumnInfo>? cached) && cached != null)
            {
                return cached;
            }

            const string sql = @"
SELECT c.name, t.name AS TypeName, c.is_nullable, c.max_length, c.is_identity, c.is_computed,
       CAST(CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END AS bit) AS IsPk
FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
LEFT JOIN (
    SELECT ic.object_id, ic.column_id
    FROM sys.index_columns ic
    JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
    WHERE i.is_primary_key = 1
) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
WHERE c.object_id = OBJECT_ID(@obj)
ORDER BY c.column_id";

            var ps = new[] { new KeyValuePair<string, object?>("@obj", table.Quoted) };

            var list = await DbHelper.ExecAsync(_module.Connection, sql, 60, ps, async cmd =>
            {
                var result = new List<ColumnInfo>();
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var type = reader.GetString(1);
                    int maxLength = reader.GetInt16(3);
                    bool unicode = type is "nvarchar" or "nchar";

                    result.Add(new ColumnInfo
                    {
                        Name = reader.GetString(0),
                        DataType = type,
                        IsNullable = reader.GetBoolean(2),
                        MaxLength = maxLength < 0 ? -1 : (unicode ? maxLength / 2 : maxLength),
                        IsPrimaryKey = reader.GetBoolean(6),
                        IsIdentity = reader.GetBoolean(4),
                        IsReadOnly = reader.GetBoolean(4) || reader.GetBoolean(5)
                                     || type is "timestamp" or "rowversion",
                        IsSkipped = SkipTypes.Contains(type),
                        IsLong = maxLength < 0 || type is "text" or "ntext" or "xml",
                        IsText = TextTypes.Contains(type),
                        IsNumber = IntTypes.Contains(type),
                        IsGuid = type == "uniqueidentifier"
                    });
                }
                return result;
            });

            _cache.Set(key, list, CacheOptions);
            return list;
        }

        public async Task<List<ForeignKeyInfo>> GetForeignKeysAsync()
        {
            var cacheKey = _module.CacheKey(CacheForeignKeys);
            if (_cache.TryGetValue(cacheKey, out List<ForeignKeyInfo>? cached) && cached != null)
            {
                return cached;
            }

            const string sql = @"
SELECT  cs.name + '.' + ct.name AS ChildTable,  cc.name AS ChildColumn,
        ps.name + '.' + pt.name AS ParentTable, pc.name AS ParentColumn
FROM sys.foreign_key_columns fkc
JOIN sys.tables  ct ON ct.object_id = fkc.parent_object_id
JOIN sys.schemas cs ON cs.schema_id = ct.schema_id
JOIN sys.columns cc ON cc.object_id = fkc.parent_object_id AND cc.column_id = fkc.parent_column_id
JOIN sys.tables  pt ON pt.object_id = fkc.referenced_object_id
JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
JOIN sys.columns pc ON pc.object_id = fkc.referenced_object_id AND pc.column_id = fkc.referenced_column_id";

            var list = await DbHelper.ExecAsync(_module.Connection, sql, 60, null, async cmd =>
            {
                var result = new List<ForeignKeyInfo>();
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    result.Add(new ForeignKeyInfo
                    {
                        ChildTable = reader.GetString(0),
                        ChildColumn = reader.GetString(1),
                        ParentTable = reader.GetString(2),
                        ParentColumn = reader.GetString(3)
                    });
                }
                return result;
            });

            _cache.Set(cacheKey, list, CacheOptions);
            return list;
        }

        /// <summary>
        /// Column names that identify a row to a person, best first. Matched against the real column
        /// list to pick what the audit log should record next to the primary key — which in these
        /// databases is usually a GUID, and a log full of GUIDs is a log nobody can read.
        /// </summary>
        private static readonly string[] LabelNames =
        {
            "username", "tendangnhap", "taikhoan", "userlogin",
            "code", "maso", "sohieu", "sochungtu", "sophieu",
            "hoten", "tennhanvien", "fullname", "ten", "name", "title", "tieude",
            "email"
        };

        public ColumnInfo? GuessLabelColumn(List<ColumnInfo> columns)
        {
            // A GUID or a number identifies nothing to a reader, so only short text qualifies.
            var usable = columns
                // Wide enough for a name or a document number, narrow enough to leave out prose.
                .Where(c => c.IsText && !c.IsLong && !c.IsSkipped && c.MaxLength is > 0 and <= 400)
                .ToList();

            if (usable.Count == 0) return null;

            foreach (var want in LabelNames)
            {
                var exact = usable.FirstOrDefault(c => string.Equals(c.Name, want, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;
            }

            // Nothing matched by name; a suffix match catches MaNhanVien, TenPhongBan and the like.
            foreach (var want in LabelNames)
            {
                var partial = usable.FirstOrDefault(c => c.Name.Contains(want, StringComparison.OrdinalIgnoreCase));
                if (partial != null) return partial;
            }

            // Still nothing recognisable — the first short text column beats no label at all.
            return usable.OrderBy(c => c.MaxLength).FirstOrDefault();
        }

        public ColumnInfo? ResolveColumn(List<ColumnInfo> columns, string? name)
            => name.IsEmpty()
                ? null
                : columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        public void ClearCache() => _cache.Reset();
    }
}

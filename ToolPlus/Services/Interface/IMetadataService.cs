using ToolPlus.Models.Explorer;

namespace ToolPlus.Services.Interface
{
    public interface IMetadataService
    {
        Task<List<TableInfo>> GetTablesAsync();

        /// <summary>Validates a client-supplied table name against the real list. Null when there is no match.</summary>
        Task<TableInfo?> ResolveTableAsync(string? name);

        Task<List<ColumnInfo>> GetColumnsAsync(TableInfo table);

        /// <summary>Validates a column name and returns the real ColumnInfo, or null.</summary>
        ColumnInfo? ResolveColumn(List<ColumnInfo> columns, string? name);

        /// <summary>Every real foreign key in the database, used to suggest links on the canvas.</summary>
        Task<List<ForeignKeyInfo>> GetForeignKeysAsync();

        void ClearCache();
    }
}

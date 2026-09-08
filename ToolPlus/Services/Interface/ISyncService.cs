using ToolPlus.Common;
using ToolPlus.Models.Explorer;
using ToolPlus.Models.Sync;

namespace ToolPlus.Services.Interface
{
    /// <summary>
    /// Copies one column from a table in another database into the same column here, pairing rows by
    /// a match column. Reads both sides, writes only the one the URL names.
    ///
    /// Every method takes the source module as a resolved <see cref="ModuleDefinition"/> rather than a
    /// key: module permission lives on the session, which only a controller can see, so the caller
    /// has to have authorised it (SyncController.SourceOrThrow). Making it a parameter of this shape
    /// means no call site can forget to.
    /// </summary>
    public interface ISyncService
    {
        /// <summary>Tables of one module — null means the module of the current request.</summary>
        Task<List<TableInfo>> TablesAsync(ModuleDefinition? module);

        /// <summary>Columns of one table — null module means the module of the current request.</summary>
        Task<List<ColumnInfo>> ColumnsAsync(ModuleDefinition? module, string table);

        /// <summary>Compares the two sides and reports what WOULD change. Writes nothing.</summary>
        Task<SyncPlanResult> PlanAsync(SyncPlanRequest req, ModuleDefinition source);

        /// <summary>
        /// Rebuilds the plan and writes the rows whose primary key the user ticked and which are
        /// still eligible. One statement per row, one transaction per batch.
        /// </summary>
        Task<SyncApplyResult> ApplyAsync(SyncApplyRequest req, ModuleDefinition source, string username, string? ip);
    }
}

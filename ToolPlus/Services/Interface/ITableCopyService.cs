using ToolPlus.Common;
using ToolPlus.Models.Copy;

namespace ToolPlus.Services.Interface
{
    /// <summary>
    /// Compares whole tables between another module and this one, row by row on the primary key, and
    /// copies the differences the user chose into this one. The module in the URL is the only side
    /// ever written.
    ///
    /// As in <see cref="ISyncService"/>, the source arrives as a resolved <see cref="ModuleDefinition"/>
    /// so the caller must already have authorised it (CrossModuleController.SourceOrThrow).
    /// </summary>
    public interface ITableCopyService
    {
        /// <summary>Tables of this module, with whether the source has them and rough row counts.</summary>
        Task<List<CopyTableStat>> TablesAsync(ModuleDefinition source);

        /// <summary>Sorts tables parents-first by this module's foreign keys.</summary>
        Task<CopyOrderResult> OrderAsync(List<string> tables);

        /// <summary>Compares one table for up to <c>Copy:StepSeconds</c>. Writes nothing.</summary>
        Task<CopyCompareStep> CompareAsync(CopyCompareRequest req, ModuleDefinition source, CancellationToken ct);

        /// <summary>
        /// Writes one phase of one table for up to <c>Copy:StepSeconds</c>, one transaction per chunk.
        /// The differences are found again from scratch, never taken from the browser.
        /// </summary>
        Task<CopyApplyStep> ApplyAsync(CopyApplyRequest req, ModuleDefinition source, string username,
            string? ip, CancellationToken ct);
    }
}

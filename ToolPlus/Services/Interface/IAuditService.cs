using ToolPlus.Models.ToolEntity;

namespace ToolPlus.Services.Interface
{
    public interface IAuditService
    {
        Task WriteAsync(AuditLogEntry entry);

        /// <summary>
        /// Writes many entries in one SaveChanges. A field sync updates thousands of rows and logs
        /// every one of them; saving per row would make the log slower than the work it records.
        /// </summary>
        Task WriteManyAsync(IEnumerable<AuditLogEntry> entries);

        /// <summary>Quick single-line entry not tied to any table (login, blueprint run…).</summary>
        Task WriteAsync(string type, string? user, string? ip, string? note = null, string? sql = null);

        /// <summary>Reads the most recent entries, newest first.</summary>
        Task<List<AuditLogEntry>> ReadLatestAsync(int take, string? type = null, string? user = null);

        /// <summary>
        /// Every entry written by one action, oldest first — the rows of a single field sync. A run
        /// can be thousands of rows, well past what the history list holds, so it is fetched on its
        /// own rather than filtered out of the latest page.
        /// </summary>
        Task<List<AuditLogEntry>> ReadRunAsync(Guid runId, int take);
    }
}

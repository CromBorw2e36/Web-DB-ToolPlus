using ToolPlus.Models.ToolEntity;

namespace ToolPlus.Services.Interface
{
    public interface IAuditService
    {
        Task WriteAsync(AuditLogEntry entry);

        /// <summary>Quick single-line entry not tied to any table (login, blueprint run…).</summary>
        Task WriteAsync(string type, string? user, string? ip, string? note = null, string? sql = null);

        /// <summary>Reads the most recent entries, newest first.</summary>
        Task<List<AuditLogEntry>> ReadLatestAsync(int take, string? type = null, string? user = null);
    }
}

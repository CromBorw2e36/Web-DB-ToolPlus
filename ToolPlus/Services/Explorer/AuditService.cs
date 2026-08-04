using Microsoft.EntityFrameworkCore;
using ToolPlus.Common;
using ToolPlus.Models.ToolEntity;
using ToolPlus.Services.Interface;

namespace ToolPlus.Services.Explorer
{
    /// <summary>
    /// Writes the log to the tool's own SQLite file. Deliberately not a table in a module database —
    /// see CLAUDE.md: a business database must never grow tables of ours.
    /// </summary>
    public class AuditService : IAuditService
    {
        /// <summary>
        /// Events that belong to the account rather than to any database — leave Module empty instead
        /// of stamping whichever module happened to be open, which would read as if signing in had a
        /// location of its own.
        /// </summary>
        private static readonly string[] AccountLevel =
            { "login", "login-fail", "password-change", "user-save", "user-delete" };

        private readonly ToolDbContext _db;
        private readonly IModuleContext _module;

        public AuditService(ToolDbContext db, IModuleContext module)
        {
            _db = db;
            _module = module;
        }

        public async Task WriteAsync(AuditLogEntry entry)
        {
            if (entry.Time == default) entry.Time = DateTime.Now;

            // Filled in here so no call site can forget: with several databases in play, a log that
            // does not say WHERE something was changed is useless.
            if (entry.Module.IsEmpty() && !AccountLevel.Contains(entry.Type))
            {
                entry.Module = _module.Current.Key;
            }

            _db.AuditLog.Add(entry);
            await _db.SaveChangesAsync();
        }

        public Task WriteAsync(string type, string? user, string? ip, string? note = null, string? sql = null)
            => WriteAsync(new AuditLogEntry
            {
                Time = DateTime.Now,
                Type = type,
                User = user,
                Ip = ip,
                Note = note,
                Sql = sql
            });

        public async Task<List<AuditLogEntry>> ReadLatestAsync(int take, string? type = null, string? user = null)
        {
            var q = _db.AuditLog.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(type)) q = q.Where(x => x.Type == type);
            if (!string.IsNullOrWhiteSpace(user)) q = q.Where(x => x.User == user);

            return await q.OrderByDescending(x => x.Id).Take(take).ToListAsync();
        }
    }
}

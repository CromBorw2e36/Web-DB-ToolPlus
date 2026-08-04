using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ToolPlus.Common;
using ToolPlus.Common.Attributes;
using ToolPlus.Models.Explorer;
using ToolPlus.Services.Interface;

namespace ToolPlus.Controllers
{
    /// <summary>
    /// Search and quick-fix data on any table. Every table and column name received from a client is
    /// validated through IMetadataService before it goes into SQL; values always travel as
    /// parameters.
    /// </summary>
    [TokenCheck]
    public class ExplorerController : BaseController
    {
        private readonly IMetadataService _meta;
        private readonly IExplorerService _explorer;
        private readonly IAuditService _audit;
        private readonly IModuleContext _module;
        private readonly ToolPlusOptions _opt;

        public ExplorerController(MemoryCacheManager memoryCache,
            IMetadataService meta, IExplorerService explorer, IAuditService audit,
            IModuleContext module, IOptions<ToolPlusOptions> opt)
            : base(memoryCache)
        {
            _meta = meta;
            _explorer = explorer;
            _audit = audit;
            _module = module;
            _opt = opt.Value;
        }

        public IActionResult Index()
        {
            ViewData["Title"] = "Data explorer";
            return View();
        }

        public async Task<IActionResult> Tables()
        {
            try
            {
                var tables = await _meta.GetTablesAsync();
                return Success(tables.Select(t => new { t.Full, t.Name, t.Schema, t.IsView }));
            }
            catch (Exception ex)
            {
                return Error(Explain(ex));
            }
        }

        public async Task<IActionResult> Columns(string table)
        {
            try
            {
                var info = await _meta.ResolveTableAsync(table);
                if (info == null) return Error($"Table \"{table}\" not found");

                return Success(new { Table = info.Full, info.IsView, Columns = await _meta.GetColumnsAsync(info) });
            }
            catch (Exception ex)
            {
                return Error(Explain(ex));
            }
        }

        [HttpPost]
        public async Task<IActionResult> Search([FromBody] SearchRequest req)
        {
            try
            {
                return Success(await _explorer.SearchAsync(req));
            }
            catch (Exception ex)
            {
                return Error(Explain(ex));
            }
        }

        [HttpPost]
        public async Task<IActionResult> Update([FromBody] UpdateRequest req)
        {
            if (!_module.CanEdit)
            {
                return Error(_opt.AllowEdit
                    ? $"Module \"{_module.Current.Name}\" is set to read-only (ToolPlus:Modules:{_module.Current.Key}:AllowEdit = false)"
                    : "The instance is in read-only mode (ToolPlus:AllowEdit = false)");
            }

            try
            {
                var changes = await _explorer.UpdateAsync(req, currentUser?.Username ?? "?", ClientIp());
                return Success(changes, $"Saved {changes.Count} column(s)");
            }
            catch (Exception ex)
            {
                return Error(Explain(ex));
            }
        }

        [HttpPost]
        public async Task<IActionResult> Query([FromBody] QueryRequest req)
        {
            try
            {
                var result = await _explorer.QueryAsync(req.Sql);
                await _audit.WriteAsync("query", currentUser?.Username, ClientIp(),
                    result.Rows.Count + " rows", result.Sql);
                return Success(result);
            }
            catch (Exception ex)
            {
                return Error(Explain(ex));
            }
        }

        public async Task<IActionResult> Log(int take = 200)
        {
            try
            {
                return Success(await _audit.ReadLatestAsync(Math.Clamp(take, 1, 2000)));
            }
            catch (Exception ex)
            {
                return Error(Explain(ex));
            }
        }

        /// <summary>Drops the metadata cache after the database structure changed (new column, new table).</summary>
        public IActionResult ClearCache()
        {
            _meta.ClearCache();
            return Success("Structure cache cleared");
        }

        private string? ClientIp()
        {
            var real = Request.Headers["X-Real-IP"].ToString();
            return real.IsEmpty() ? HttpContext.Connection.RemoteIpAddress?.ToString() : real;
        }

        /// <summary>
        /// Our own errors are shown verbatim; for SQL errors take the innermost message, because that
        /// is the one naming the cause (wrong type, FK violation, timeout…) — the audience for this
        /// tool can read them.
        /// </summary>
        private static string Explain(Exception ex)
            => ex is InvalidOperationException ? ex.Message : ex.GetBaseException().Message;
    }
}

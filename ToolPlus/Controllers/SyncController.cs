using Microsoft.AspNetCore.Mvc;
using ToolPlus.Common;
using ToolPlus.Common.Attributes;
using ToolPlus.Models.Sync;
using ToolPlus.Services.Interface;

namespace ToolPlus.Controllers
{
    /// <summary>
    /// Repairs a column from another database: pair the rows by a match column, look at what would
    /// change, then write the ones the user ticked.
    ///
    /// The database written to is always the module in the URL. The other one is only ever read.
    /// </summary>
    [TokenCheck]
    public class SyncController : CrossModuleController
    {
        private readonly ISyncService _sync;
        private readonly IModuleContext _module;

        public SyncController(MemoryCacheManager memoryCache, ISyncService sync, IModuleContext module,
            ModuleRegistry registry, IUserService users)
            : base(memoryCache, registry, users)
        {
            _sync = sync;
            _module = module;
        }

        public IActionResult Index()
        {
            ViewData["Title"] = "Đồng bộ trường từ DB khác";
            ViewData["Fill"] = true;
            ViewData["Sources"] = ReadableModules()
                .Select(m => new { m.Key, m.Name, Current = m.Key == _module.Current.Key })
                .ToList();
            return View();
        }

        // -------------------------------------------------------------------- api

        /// <summary>Tables of a module. No module named = the one being written to.</summary>
        // The query parameter must NOT be called "module": model binding reads route values before
        // the query string, so {module} in the URL (the target) would silently win over it.
        public async Task<IActionResult> Tables(string? source)
        {
            try
            {
                var m = source.IsEmpty() ? null : SourceOrThrow(source);
                var tables = await _sync.TablesAsync(m);
                return Success(tables.Select(t => new { t.Full, t.Name, t.IsView }));
            }
            catch (Exception ex)
            {
                return Error(Message(ex));
            }
        }

        public async Task<IActionResult> Columns(string? source, string table)
        {
            try
            {
                var m = source.IsEmpty() ? null : SourceOrThrow(source);
                return Success(new { Table = table, Columns = await _sync.ColumnsAsync(m, table) });
            }
            catch (Exception ex)
            {
                return Error(Message(ex));
            }
        }

        [HttpPost]
        public async Task<IActionResult> Plan([FromBody] SyncPlanRequest req)
        {
            try
            {
                return Success(await _sync.PlanAsync(req, SourceOrThrow(req.SourceModule)));
            }
            catch (Exception ex)
            {
                return Error(Message(ex));
            }
        }

        [HttpPost]
        public async Task<IActionResult> Apply([FromBody] SyncApplyRequest req)
        {
            try
            {
                var result = await _sync.ApplyAsync(req, SourceOrThrow(req.SourceModule),
                    currentUser?.Username ?? "?", ClientIp());

                return Success(result, $"Đã ghi {result.Updated} dòng");
            }
            catch (Exception ex)
            {
                return Error(Message(ex));
            }
        }
    }
}

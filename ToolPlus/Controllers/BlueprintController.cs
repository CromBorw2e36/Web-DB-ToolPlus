using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ToolPlus.Common;
using ToolPlus.Common.Attributes;
using ToolPlus.Models.Blueprints;
using ToolPlus.Services.Interface;
using ToolPlus.Models.ToolEntity;

namespace ToolPlus.Controllers
{
    /// <summary>
    /// Blueprints: drop tables on a canvas, link field to field, save for reuse.
    /// They live in the tool's own SQLite file; the module database gains nothing.
    /// </summary>
    [TokenCheck]
    public class BlueprintController : BaseController
    {
        private readonly IBlueprintService _blueprint;
        private readonly IBlueprintRunner _runner;
        private readonly IMetadataService _meta;
        private readonly IAuditService _audit;
        private readonly IModuleContext _module;
        private readonly ToolPlusOptions _opt;

        public BlueprintController(MemoryCacheManager memoryCache,
            IBlueprintService blueprint, IBlueprintRunner runner, IMetadataService meta,
            IAuditService audit, IModuleContext module, IOptions<ToolPlusOptions> opt)
            : base(memoryCache)
        {
            _blueprint = blueprint;
            _runner = runner;
            _meta = meta;
            _audit = audit;
            _module = module;
            _opt = opt.Value;
        }

        /// <summary>The list of blueprints.</summary>
        public async Task<IActionResult> Index()
        {
            ViewData["Title"] = "Blueprints";
            ViewData["List"] = await _blueprint.ListAsync();
            return View();
        }

        /// <summary>The designer page. No id = a new blueprint.</summary>
        public async Task<IActionResult> Ve(Guid? id)
        {
            var row = id.HasValue && id.Value != Guid.Empty ? await _blueprint.GetAsync(id.Value) : null;

            ViewData["Title"] = row == null ? "New blueprint" : "Edit: " + row.Name;
            ViewData["Fill"] = true;
            ViewData["Blueprint"] = row;
            ViewData["GraphJson"] = row?.GraphJson ?? "{}";
            return View();
        }

        /// <summary>The run page: fill in parameters, run, then read and edit the results.</summary>
        public async Task<IActionResult> Chay(Guid id)
        {
            var row = await _blueprint.GetAsync(id);
            if (row == null) return Redirect(_module.Url("/blueprint"));

            ViewData["Title"] = row.Name;
            ViewData["Fill"] = true;
            ViewData["Blueprint"] = row;
            ViewData["Graph"] = await _blueprint.GetGraphAsync(id);
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Run([FromBody] BlueprintRunRequest req)
        {
            try
            {
                var graph = await _blueprint.GetGraphAsync(req.Id);
                if (graph.Nodes.Count == 0) return Error("Blueprint has no tables");

                var result = await _runner.RunAsync(graph, req.Params ?? new(), _module.CanEdit);

                await _audit.WriteAsync("blueprint-run", currentUser?.Username, ClientIp(),
                    _module.Current.Key + " · " + req.Id + " · "
                    + result.Nodes.Sum(n => n.Rows.Count) + " rows / " + result.ElapsedMs + "ms");

                return Success(result);
            }
            catch (Exception ex)
            {
                return Error(ex is InvalidOperationException ? ex.Message : ex.GetBaseException().Message);
            }
        }

        // -------------------------------------------------------------------- api

        public async Task<IActionResult> Get(Guid id)
        {
            try
            {
                var row = await _blueprint.GetAsync(id);
                if (row == null) return Error("Blueprint not found");

                return Success(new
                {
                    row.Id, row.Name, row.Description, row.Icon, row.Pinned, row.IsShared,
                    Graph = await _blueprint.GetGraphAsync(id)
                });
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        public class SaveRequest
        {
            public Guid Id { get; set; }
            public string Name { get; set; } = "";
            public string Description { get; set; } = "";
            public string Icon { get; set; } = "i-panel";
            public bool Pinned { get; set; }
            public bool IsShared { get; set; } = true;
            public BlueprintGraph Graph { get; set; } = new();
        }

        [HttpPost]
        public async Task<IActionResult> Save([FromBody] SaveRequest req)
        {
            try
            {
                var row = await _blueprint.SaveAsync(new Blueprint
                {
                    Id = req.Id,
                    Name = req.Name,
                    Description = req.Description,
                    Icon = req.Icon,
                    Pinned = req.Pinned,
                    IsShared = req.IsShared
                }, req.Graph, currentUser?.Username ?? "?");

                return Success(new { row.Id, row.Name }, "Blueprint saved");
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        [HttpPost]
        public async Task<IActionResult> Delete(Guid id)
        {
            try
            {
                await _blueprint.DeleteAsync(id, currentUser?.Username ?? "?");
                return Success("Blueprint deleted");
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        [HttpPost]
        public async Task<IActionResult> Duplicate(Guid id)
        {
            try
            {
                var copy = await _blueprint.DuplicateAsync(id, currentUser?.Username ?? "?");
                return Success(new { copy.Id, copy.Name }, "Blueprint duplicated");
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        [HttpPost]
        public async Task<IActionResult> Pin(Guid id, bool pinned)
        {
            try
            {
                await _blueprint.SetPinnedAsync(id, pinned);
                return Success(pinned ? "Pinned to menu" : "Unpinned");
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        /// <summary>Table list for the palette — leaner than /explorer/tables.</summary>
        public async Task<IActionResult> Tables()
        {
            try
            {
                var tables = await _meta.GetTablesAsync();
                return Success(tables.Select(t => new { t.Full, t.Name, t.IsView }));
            }
            catch (Exception ex)
            {
                return Error(ex.GetBaseException().Message);
            }
        }

        /// <summary>A table's columns, used to build its node on the canvas.</summary>
        public async Task<IActionResult> Columns(string table)
        {
            try
            {
                var info = await _meta.ResolveTableAsync(table);
                if (info == null) return Error($"Table \"{table}\" not found");

                return Success(new
                {
                    Table = info.Full,
                    info.Name,
                    info.IsView,
                    Columns = await _meta.GetColumnsAsync(info)
                });
            }
            catch (Exception ex)
            {
                return Error(ex.GetBaseException().Message);
            }
        }

        private string? ClientIp()
        {
            var real = Request.Headers["X-Real-IP"].ToString();
            return real.IsEmpty() ? HttpContext.Connection.RemoteIpAddress?.ToString() : real;
        }

        /// <summary>Suggests links between the table just added and the ones already on the canvas.</summary>
        public async Task<IActionResult> Suggest(string table, string? others)
        {
            try
            {
                var list = (others ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                return Success(await _blueprint.SuggestAsync(table, list));
            }
            catch (Exception ex)
            {
                return Error(ex.GetBaseException().Message);
            }
        }
    }
}

using Microsoft.AspNetCore.Mvc;
using ToolPlus.Common;
using ToolPlus.Common.Attributes;
using ToolPlus.Models.Users;
using ToolPlus.Services.Interface;

namespace ToolPlus.Controllers
{
    /// <summary>
    /// Manages ToolPlus accounts and their permission on each module.
    /// Admins only — left open, this is the page where anyone grants themselves everything.
    /// </summary>
    [TokenCheck(AdminOnly = true)]
    public class UserController : BaseController
    {
        private readonly IUserService _users;
        private readonly ModuleRegistry _registry;

        public UserController(MemoryCacheManager memoryCache, IUserService users, ModuleRegistry registry)
            : base(memoryCache)
        {
            _users = users;
            _registry = registry;
        }

        public IActionResult Index()
        {
            ViewData["Title"] = "Users";
            return View();
        }

        public async Task<IActionResult> DanhSach()
        {
            try
            {
                return Success(new
                {
                    Users = await _users.ListAsync(),
                    // The module list travels with the users so the permission grid always matches the
                    // configured ConnectionStrings: add a connection string and a column appears.
                    Modules = _registry.All.Select(m => new { m.Key, m.Name, m.AllowEdit }),
                    Me = currentUser?.Id
                });
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        [HttpPost]
        public async Task<IActionResult> Luu([FromBody] SaveUserRequest req)
        {
            try
            {
                // Reject junk keys: only modules that really have a connection string are accepted.
                req.Modules = req.Modules
                    .Where(m => _registry.Find(m.ModuleKey) != null)
                    .ToList();

                var id = await _users.SaveAsync(req, currentUser?.Username ?? "?");
                return Success(new { Id = id }, "Saved");
            }
            catch (InvalidOperationException ex)
            {
                return Error(ex.Message);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        [HttpPost]
        public async Task<IActionResult> Xoa(Guid id)
        {
            try
            {
                await _users.DeleteAsync(id, currentUser?.Username ?? "?");
                return Success("Deleted");
            }
            catch (InvalidOperationException ex)
            {
                return Error(ex.Message);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }
    }
}

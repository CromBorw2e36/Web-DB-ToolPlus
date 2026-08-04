using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ToolPlus.Common;
using ToolPlus.Common.Attributes;
using ToolPlus.Models.Users;
using ToolPlus.Services.Interface;

namespace ToolPlus.Controllers
{
    /// <summary>
    /// Sign-in against ToolPlus's own accounts (the ToolUser table in SQLite).
    ///
    /// Identity is deliberately not borrowed from the applications that own the data: a module is
    /// then only a source of data, and permissions can be stored per module — which a flat list of
    /// allowed usernames in configuration could never express.
    /// </summary>
    public class LoginController : BaseController
    {
        private const int MaxFail = 5;
        private static readonly TimeSpan FailWindow = TimeSpan.FromMinutes(5);

        private readonly ToolPlusOptions _opt;
        private readonly IUserService _users;
        private readonly IAuditService _audit;

        public LoginController(MemoryCacheManager memoryCache, IOptions<ToolPlusOptions> opt,
            IUserService users, IAuditService audit)
            : base(memoryCache)
        {
            _opt = opt.Value;
            _users = users;
            _audit = audit;
        }

        public IActionResult Index(string? returnUrl)
        {
            var token = Request.Cookies[_opt.CookieName];
            if (!token.IsEmpty() && _users.ResolveSession(token!) != null)
            {
                return Redirect(SafeReturn(returnUrl));
            }

            ViewData["ReturnUrl"] = SafeReturn(returnUrl);
            ViewData["Options"] = _opt;
            return View();
        }

        public IActionResult Denied()
        {
            ViewData["Options"] = _opt;
            ViewData["DeniedMessage"] = TempData["DeniedMessage"] as string;
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Authenticate(string? username, string? password, string? returnUrl)
        {
            if (username.IsEmpty() || password.IsEmpty())
            {
                return Error("Username and password are required");
            }

            var ip = ClientIp();
            var failKey = "tp-fail-" + username!.ToLowerInvariant() + "-" + ip;
            if (memoryCache.TryGetValue(failKey, out int fails) && fails >= MaxFail)
            {
                return Error($"Too many failed attempts ({MaxFail}), try again in {FailWindow.TotalMinutes:0} minutes");
            }

            var (status, user) = _users.Authenticate(username!, password!);

            if (status != LoginStatus.Ok || user == null)
            {
                // Only wrong passwords are counted. Counting a locked or expired account would lock
                // the user out for another 5 minutes over something they cannot fix themselves.
                if (status == LoginStatus.WrongCredentials)
                {
                    memoryCache.Set(failKey, fails + 1, new MemoryCacheEntryOptions().SetAbsoluteExpiration(FailWindow));
                    await _audit.WriteAsync("login-fail", username, ip);
                }

                return Error(status switch
                {
                    LoginStatus.Locked => "Account is locked — contact your ToolPlus administrator",
                    LoginStatus.Expired => "Account has expired — contact your ToolPlus administrator",
                    _ => "Invalid username or password"
                });
            }

            memoryCache.Remove(failKey);

            var token = _users.CreateSession(user, ip, DeviceId(), Request.Headers.UserAgent.ToString());

            Response.Cookies.Append(_opt.CookieName, token, new CookieOptions
            {
                Expires = DateTime.Now.AddDays(Math.Max(1, _opt.SessionDays)),
                HttpOnly = true,
                SameSite = SameSiteMode.Lax
            });

            await _audit.WriteAsync("login", user.Username, ip);

            var url = user.MustChangePassword ? "/login/doimatkhau" : SafeReturn(returnUrl);
            return Success(new { Url = url }, "Signed in");
        }

        public IActionResult Logout()
        {
            var cookie = Request.Cookies[_opt.CookieName];
            if (!cookie.IsEmpty()) _users.RevokeSession(cookie!);

            Response.Cookies.Delete(_opt.CookieName);
            return Redirect("/login/index");
        }

        /// <summary>
        /// Self-service password change. SkipPasswordChange is what lets an account that is being
        /// forced to change its password actually open this page instead of redirecting in a loop.
        /// </summary>
        [TokenCheck(SkipPasswordChange = true, SkipModuleCheck = true)]
        public IActionResult DoiMatKhau()
        {
            ViewData["Title"] = "Change password";
            ViewData["Forced"] = currentUser?.MustChangePassword ?? false;
            return View();
        }

        [HttpPost]
        [TokenCheck(SkipPasswordChange = true, SkipModuleCheck = true)]
        public async Task<IActionResult> DoiMatKhauAPI([FromBody] ChangePasswordRequest req)
        {
            try
            {
                await _users.ChangePasswordAsync(currentUser!.Id, req.OldPassword, req.NewPassword, sessionToken);
                return Success(new { Url = "/home/index" }, "Password changed");
            }
            catch (InvalidOperationException ex)
            {
                return Error(ex.Message);
            }
        }

        // ---------------------------------------------------------------- helper

        /// <summary>Device id kept in its own cookie, so each machine gets its own session instead of stealing another's.</summary>
        private string DeviceId()
        {
            var id = Request.Cookies["tp_device"];
            if (id.IsEmpty() || !Guid.TryParse(id, out _))
            {
                id = Guid.NewGuid().ToString();
                Response.Cookies.Append("tp_device", id,
                    new CookieOptions { Expires = DateTime.Now.AddYears(2), HttpOnly = true, SameSite = SameSiteMode.Lax });
            }
            return id!;
        }

        private string ClientIp()
        {
            var real = Request.Headers["X-Real-IP"].ToString();
            return real.IsEmpty() ? HttpContext.Connection.RemoteIpAddress?.ToString() ?? "" : real;
        }

        /// <summary>Local paths only, so the parameter cannot be abused as an open redirect.</summary>
        private string SafeReturn(string? url)
            => !url.IsEmpty() && Url.IsLocalUrl(url) ? url! : "/";
    }
}

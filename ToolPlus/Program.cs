using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json.Serialization;
using ToolPlus.Common;
using ToolPlus.Models.ToolEntity;
using ToolPlus.Services.Blueprints;
using ToolPlus.Services.Copy;
using ToolPlus.Services.Explorer;
using ToolPlus.Services.Interface;
using ToolPlus.Services.Sync;
using ToolPlus.Services.Users;

var builder = WebApplication.CreateBuilder(args);

// Connection strings and the bootstrap password live in a local file (gitignored) so they never
// reach the repository. Loaded last, so it overrides every other source.
// See appsettings.local.example.json.
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

// Newtonsoft + DefaultContractResolver keeps property names in PascalCase, which is what the
// frontend expects: every JS file reads json.Status / json.Data / json.Message verbatim.
builder.Services.AddControllersWithViews()
    .AddNewtonsoftJson(o => o.SerializerSettings.ContractResolver = new DefaultContractResolver());

// MemoryCacheManager replaces the default IMemoryCache to add Reset() / GenerateKey().
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<MemoryCacheManager>();
builder.Services.AddSingleton<IMemoryCache>(sp => sp.GetRequiredService<MemoryCacheManager>());

builder.Services.Configure<ToolPlusOptions>(builder.Configuration.GetSection(ToolPlusOptions.SectionName));

// One connection string is one module. The list is built at startup so bad configuration fails
// here rather than surfacing the first time a user clicks something.
var toolOptions = builder.Configuration.GetSection(ToolPlusOptions.SectionName).Get<ToolPlusOptions>()
                  ?? new ToolPlusOptions();
builder.Services.AddSingleton(new ModuleRegistry(builder.Configuration, toolOptions));

// The request's module comes from the {module} route value; ModuleContext holds exactly one
// connection for the whole request and disposes it when the scope ends.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IModuleContext, ModuleContext>();
builder.Services.AddRouting(o => o.ConstraintMap.Add("module", typeof(ModuleRouteConstraint)));

// Services read structure and rows through the narrower IModuleScope, so the same code can be
// pointed at a SECOND database — which is what comparing a field across two of them needs.
builder.Services.AddScoped<IModuleScope>(sp => sp.GetRequiredService<IModuleContext>());
builder.Services.AddSingleton<IModuleScopeFactory, ModuleScopeFactory>();

// The tool's own store: SQLite next to the app. A module database must never grow tables of ours,
// so blueprints / audit log / relation hints all live here.
var toolDbPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "toolplus.db");
Directory.CreateDirectory(Path.GetDirectoryName(toolDbPath)!);
builder.Services.AddDbContext<ToolDbContext>(o => o.UseSqlite("Data Source=" + toolDbPath));

builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IMetadataService, MetadataService>();
builder.Services.AddScoped<IExplorerService, ExplorerService>();
builder.Services.AddScoped<IBlueprintService, BlueprintService>();
builder.Services.AddScoped<IBlueprintRunner, BlueprintRunner>();
builder.Services.AddScoped<ISyncService, SyncService>();
builder.Services.AddScoped<ITableCopyService, TableCopyService>();

var app = builder.Build();

// Small SQLite schema that rarely changes -> EnsureCreated, no migrations.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ToolDbContext>();
    db.Database.EnsureCreated();

    // EnsureCreated skips a file that already exists, so the identity tables have to be created by
    // hand — otherwise an installation from before they existed silently lacks them.
    db.EnsureIdentityTables();
    db.EnsureModuleColumns(scope.ServiceProvider.GetRequiredService<ModuleRegistry>().Default.Key);
    db.EnsureAuditColumns();

    var boot = toolOptions.Bootstrap;

    if (!db.ToolUser.Any())
    {
        if (!boot.IsValid)
        {
            app.Logger.LogWarning(
                "No accounts exist and ToolPlus:Bootstrap is empty — nobody will be able to sign in. "
                + "Set Bootstrap:Username/Password in appsettings.local.json and restart.");
        }
        else
        {
            db.ToolUser.Add(new ToolUser
            {
                Id = Guid.NewGuid(),
                Username = boot.Username.Trim(),
                PasswordHash = UserService.Hash(boot.Password),
                FullName = boot.FullName.IsEmpty() ? boot.Username.Trim() : boot.FullName.Trim(),
                IsAdmin = true,
                IsActive = true,
                CreatedAt = DateTime.Now,
                CreatedBy = "bootstrap"
            });
            db.SaveChanges();

            app.Logger.LogWarning("Created the first administrator \"{User}\" from ToolPlus:Bootstrap.",
                boot.Username);
        }
    }
    else if (!boot.Password.IsEmpty())
    {
        // A password left in a config file is a password everyone forgets about — keep nagging.
        app.Logger.LogWarning(
            "ToolPlus:Bootstrap:Password is still in the configuration even though accounts exist. "
            + "Remove it; accounts are managed at /user.");
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

// Intended for internal networks behind whatever terminates TLS in front of it, so no
// UseHttpsRedirection/UseHsts here.
app.UseStaticFiles();
app.UseRouting();

// The module is the first URL segment: /billing/explorer/index. This must be registered BEFORE the
// default route, and the ":module" constraint is what stops "/explorer/index" from being read as
// module = explorer.
app.MapControllerRoute(
    name: "module",
    pattern: "{module:module}/{controller=Home}/{action=Index}/{id?}");

// The module-less route is kept on purpose: the login page belongs to no module, and an older URL
// somebody bookmarked falls back to the default module instead of 404.
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();

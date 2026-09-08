using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace ToolPlus.Common
{
    /// <summary>
    /// A module and a connection to its database — everything the data layer ever needs to read
    /// structure and rows.
    ///
    /// Split out of <see cref="IModuleContext"/> so a service can also work on a module that is NOT
    /// the one in the URL: comparing a field across two databases needs a second connection, and
    /// the request's own context can only ever hold one.
    /// </summary>
    public interface IModuleScope
    {
        ModuleDefinition Module { get; }

        /// <summary>One connection shared by the whole request, the way EF does it.</summary>
        DbConnection Connection { get; }

        /// <summary>
        /// Prefixes a cache key with the module. REQUIRED for everything read from a database:
        /// two modules can both have dbo.sys_User, and a shared cache would serve one module's
        /// structure for the other.
        /// </summary>
        string CacheKey(string suffix);
    }

    /// <summary>
    /// The module the current request belongs to, together with a connection to its database.
    ///
    /// This is deliberately not an EF DbContext: the data layer never maps entities of a business
    /// database, it only ever needs one connection — which is what lets ToolPlus work against any
    /// schema without scaffolding anything.
    /// </summary>
    public interface IModuleContext : IModuleScope
    {
        ModuleDefinition Current { get; }

        /// <summary>
        /// Whether the URL named a module or we fell back to the default. Needed to tell "this
        /// user has no permission" apart from "this user has not said where they want to go" — in
        /// the latter case, send them to a module they can access instead of blocking them.
        /// </summary>
        bool FromRoute { get; }

        /// <summary>
        /// Whether this module's data may be edited — the AND of three layers: the global flag,
        /// the module's own flag, and the signed-in user's permission.
        /// </summary>
        bool CanEdit { get; }

        /// <summary>
        /// Write permission taken from ToolUserModule, assigned by TokenCheckAttribute after
        /// authentication. Defaults to true so that any path not going through the attribute is
        /// not blocked by a value nobody ever set.
        /// </summary>
        bool UserCanEdit { get; set; }

        /// <summary>
        /// An internal path carrying the current module: "/explorer/index" -&gt;
        /// "/billing/explorer/index". Every link in the UI must go through this, otherwise one
        /// click silently drops the user back to the default module.
        ///
        /// /login/* is passed through unchanged — signing in belongs to no module.
        /// </summary>
        string Url(string path);
    }

    public class ModuleContext : IModuleContext, IDisposable, IAsyncDisposable
    {
        private readonly ModuleRegistry _registry;
        private readonly ToolPlusOptions _options;
        private readonly IHttpContextAccessor _http;

        private ModuleDefinition? _current;
        private bool _fromRoute;
        private SqlConnection? _conn;

        public ModuleContext(ModuleRegistry registry, IOptions<ToolPlusOptions> options, IHttpContextAccessor http)
        {
            _registry = registry;
            _options = options.Value;
            _http = http;
        }

        /// <summary>
        /// Taken from the "{module}" route value. A route that does not declare that segment falls
        /// back to the default module, which is what allows pages to be migrated to module-aware
        /// URLs one at a time.
        /// </summary>
        public ModuleDefinition Current
        {
            get
            {
                if (_current != null) return _current;

                var fromRoute = _registry.Find(_http.HttpContext?.Request.RouteValues["module"]?.ToString());
                _fromRoute = fromRoute != null;
                _current = fromRoute ?? _registry.Default;
                return _current;
            }
        }

        public ModuleDefinition Module => Current;

        public bool FromRoute
        {
            get
            {
                _ = Current;    // force resolution first, or this wrongly reports false
                return _fromRoute;
            }
        }

        public bool UserCanEdit { get; set; } = true;

        public bool CanEdit => _options.AllowEdit && Current.AllowEdit && UserCanEdit;

        public DbConnection Connection => _conn ??= new SqlConnection(Current.ConnectionString);

        public string CacheKey(string suffix) => Current.Key + ":" + suffix;

        public string Url(string path)
        {
            if (path.IsEmpty()) return "/" + Current.Key;
            if (!path.StartsWith('/')) path = "/" + path;
            if (path.StartsWith("/login/", StringComparison.OrdinalIgnoreCase)) return path;

            return "/" + Current.Key + path;
        }

        public void Dispose()
        {
            _conn?.Dispose();
            _conn = null;
        }

        public async ValueTask DisposeAsync()
        {
            if (_conn != null) await _conn.DisposeAsync();
            _conn = null;
        }
    }

    /// <summary>
    /// A module opened ALONGSIDE the request's own — the second database when a field is compared
    /// across two of them.
    ///
    /// Read-only by construction: it hands out a connection and a cache prefix and nothing else,
    /// so no write path can reach a database the URL does not name (and that the audit log would
    /// therefore record under the wrong module). The caller owns it and must dispose it.
    /// </summary>
    public sealed class SecondaryModuleScope : IModuleScope, IDisposable, IAsyncDisposable
    {
        private SqlConnection? _conn;

        public SecondaryModuleScope(ModuleDefinition module) => Module = module;

        public ModuleDefinition Module { get; }

        public DbConnection Connection => _conn ??= new SqlConnection(Module.ConnectionString);

        public string CacheKey(string suffix) => Module.Key + ":" + suffix;

        public void Dispose()
        {
            _conn?.Dispose();
            _conn = null;
        }

        public async ValueTask DisposeAsync()
        {
            if (_conn != null) await _conn.DisposeAsync();
            _conn = null;
        }
    }

    public interface IModuleScopeFactory
    {
        /// <summary>
        /// Opens a second module for reading. It does NOT check that the signed-in user may read
        /// that module — permission lives on the session, so the caller has to have authorised the
        /// key first (see SyncController.SourceOrThrow).
        /// </summary>
        SecondaryModuleScope Open(string? moduleKey);
    }

    public class ModuleScopeFactory : IModuleScopeFactory
    {
        private readonly ModuleRegistry _registry;

        public ModuleScopeFactory(ModuleRegistry registry) => _registry = registry;

        public SecondaryModuleScope Open(string? moduleKey)
            => new(_registry.Find(moduleKey)
                   ?? throw new InvalidOperationException($"Module \"{moduleKey}\" does not exist"));
    }
}

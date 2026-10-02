namespace ToolPlus.Common
{
    /// <summary>Bound from the "ToolPlus" section of appsettings.json.</summary>
    public class ToolPlusOptions
    {
        public const string SectionName = "ToolPlus";

        /// <summary>
        /// Per-module extras (display name, icon, whether editing is allowed). The key is the
        /// connection string name in ConnectionStrings. A module with no entry still works, and
        /// falls back to the connection string name as its display name.
        /// </summary>
        public Dictionary<string, ModuleMeta> Modules { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Module opened when the URL does not name one. Empty = first connection string.</summary>
        public string DefaultModule { get; set; } = "";

        /// <summary>
        /// Optional link to the application that owns the data, shown as a convenience on the
        /// login page. Left empty on purpose: ToolPlus does not need to know about that app.
        /// </summary>
        public HostAppLink HostApp { get; set; } = new();

        /// <summary>
        /// Name of ToolPlus's session cookie. Keep it different from the cookie of any other app
        /// on the same host: cookies are not scoped by port, so two apps sharing a cookie name
        /// on localhost will keep signing each other out.
        /// </summary>
        public string CookieName { get; set; } = "tp_token";

        /// <summary>How many days a session lives before requiring a fresh login.</summary>
        public int SessionDays { get; set; } = 30;

        /// <summary>
        /// Administrator created automatically while the ToolUser table is empty — otherwise a
        /// fresh install would have no way in. Clear this password once you can sign in.
        /// </summary>
        public BootstrapAccount Bootstrap { get; set; } = new();

        /// <summary>Accent-insensitive collation for the search box. Empty = accents are significant.</summary>
        public string SearchCollation { get; set; } = "Vietnamese_CI_AI";

        /// <summary>Cap on rows returned per request, so nobody drags a whole table into the browser.</summary>
        public int MaxRows { get; set; } = 500;

        public int QueryTimeoutSeconds { get; set; } = 60;

        /// <summary>Whether data can be edited at all. Off = the whole instance is read-only.</summary>
        public bool AllowEdit { get; set; } = true;

        public SyncOptions Sync { get; set; } = new();

        public CopyOptions Copy { get; set; } = new();
    }

    /// <summary>Limits for the table-copy page (/&lt;module&gt;/copy).</summary>
    public class CopyOptions
    {
        /// <summary>Rows read per page from each side, and written per transaction.</summary>
        public int ChunkSize { get; set; } = 1000;

        /// <summary>
        /// How long one request keeps working before handing a cursor back to the browser. A big
        /// table takes minutes; one long request would be cut by whatever proxy sits in front of this
        /// tool, and the page would have no progress to show.
        /// </summary>
        public int StepSeconds { get; set; } = 15;

        /// <summary>Differing rows listed per kind (missing / changed / extra) for the user to tick.</summary>
        public int MaxListedRows { get; set; } = 500;

        /// <summary>Tables bigger than this (approximate count, either side) are refused.</summary>
        public long MaxRowsPerTable { get; set; } = 5_000_000;
    }

    /// <summary>Limits for the field-sync page (/&lt;module&gt;/sync).</summary>
    public class SyncOptions
    {
        /// <summary>
        /// Rows compared in one run. Deliberately separate from <see cref="ToolPlusOptions.MaxRows"/>:
        /// that caps what a human reads in a grid, while a sync sweeps a whole slice of a table, where
        /// 500 would be uselessly small. Hitting the cap is reported, not silently truncated.
        /// </summary>
        public int MaxRows { get; set; } = 5000;

        /// <summary>Rows written per transaction. Small enough that a failure rolls back little.</summary>
        public int BatchSize { get; set; } = 100;
    }

    /// <summary>Convenience link back to the application that owns the data being browsed.</summary>
    public class HostAppLink
    {
        /// <summary>Label for the link. Falls back to the URL host when empty.</summary>
        public string Name { get; set; } = "";

        public string Url { get; set; } = "";

        public bool IsValid => !Url.IsEmpty();
    }

    public class BootstrapAccount
    {
        public string Username { get; set; } = "";

        public string Password { get; set; } = "";

        public string FullName { get; set; } = "";

        public bool IsValid => !Username.IsEmpty() && !Password.IsEmpty();
    }
}

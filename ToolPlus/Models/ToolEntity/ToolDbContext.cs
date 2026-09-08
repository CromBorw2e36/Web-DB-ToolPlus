using Microsoft.EntityFrameworkCore;

namespace ToolPlus.Models.ToolEntity;

/// <summary>
/// The tool's own store (SQLite, file App_Data/toolplus.db).
///
/// Kept completely separate from the module databases: those hold somebody's real business data and
/// must never grow a table of ours. Everything ToolPlus generates lives here.
/// </summary>
public class ToolDbContext : DbContext
{
    public ToolDbContext(DbContextOptions<ToolDbContext> options) : base(options) { }

    public DbSet<Blueprint> Blueprint => Set<Blueprint>();

    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    public DbSet<RelationHint> RelationHint => Set<RelationHint>();

    public DbSet<ToolUser> ToolUser => Set<ToolUser>();

    public DbSet<ToolSession> ToolSession => Set<ToolSession>();

    public DbSet<ToolUserModule> ToolUserModule => Set<ToolUserModule>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Blueprint>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ModuleKey, x.Name });
            e.Property(x => x.Name).IsRequired();
            e.Property(x => x.GraphJson).IsRequired();
        });

        b.Entity<AuditLogEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Time);
            e.HasIndex(x => x.Type);
            // Every row of one sync is read back together to build an undo script.
            e.HasIndex(x => x.RunId);
        });

        b.Entity<RelationHint>(e =>
        {
            e.HasKey(x => x.Id);
            // A column pair is recorded once per module; later links only bump UseCount.
            e.HasIndex(x => new { x.ModuleKey, x.FromTable, x.FromColumn, x.ToTable, x.ToColumn })
                .IsUnique();
        });

        b.Entity<ToolUser>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Username).IsRequired();
            e.Property(x => x.PasswordHash).IsRequired();
        });

        b.Entity<ToolSession>(e =>
        {
            e.HasKey(x => x.Id);
            // Every request looks a session up by this column -> it needs an index, and a token must
            // exist exactly once.
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.Property(x => x.TokenHash).IsRequired();
        });

        b.Entity<ToolUserModule>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.ModuleKey }).IsUnique();
            e.Property(x => x.ModuleKey).IsRequired();
        });
    }

    /// <summary>
    /// Adds the module column to tables that predate it. Same reason as EnsureIdentityTables:
    /// EnsureCreated() skips an existing file, and deleting the file would lose every blueprint.
    ///
    /// Blueprints and hints from before modules existed are assigned to the default module — which
    /// is in fact the database they were drawn against.
    /// </summary>
    public void EnsureModuleColumns(string defaultModuleKey)
    {
        AddColumnIfMissing("Blueprint", "ModuleKey", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("RelationHint", "ModuleKey", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("AuditLog", "Module", "TEXT NOT NULL DEFAULT ''");

        Database.ExecuteSql($"UPDATE Blueprint SET ModuleKey = {defaultModuleKey} WHERE ModuleKey = ''");
        Database.ExecuteSql($"UPDATE RelationHint SET ModuleKey = {defaultModuleKey} WHERE ModuleKey = ''");

        // The old index lacked ModuleKey, so two modules could not each learn the same column pair.
        Database.ExecuteSqlRaw("""
            DROP INDEX IF EXISTS IX_RelationHint_FromTable_FromColumn_ToTable_ToColumn;
            CREATE UNIQUE INDEX IF NOT EXISTS IX_RelationHint_ModuleKey_FromTable_FromColumn_ToTable_ToColumn
                ON RelationHint (ModuleKey, FromTable, FromColumn, ToTable, ToColumn);
            CREATE INDEX IF NOT EXISTS IX_Blueprint_ModuleKey_Name ON Blueprint (ModuleKey, Name);
            """);
    }

    /// <summary>
    /// Same reason as EnsureIdentityTables: EnsureCreated() skips a file that already exists, so a
    /// column introduced later never appears on an installation that ran an earlier build.
    /// </summary>
    public void EnsureAuditColumns()
    {
        AddColumnIfMissing("AuditLog", "RunId", "TEXT NULL");
        AddColumnIfMissing("AuditLog", "Label", "TEXT NULL");
        Database.ExecuteSqlRaw(
            "CREATE INDEX IF NOT EXISTS IX_AuditLog_RunId ON AuditLog (RunId);");
    }

    /// <summary>SQLite has no ADD COLUMN IF NOT EXISTS — PRAGMA has to be asked first.</summary>
    private void AddColumnIfMissing(string table, string column, string definition)
    {
        var conn = Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();

        using (var check = conn.CreateCommand())
        {
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
            if (Convert.ToInt32(check.ExecuteScalar()) > 0) return;
        }

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// EnsureCreated() only builds tables when the file does not exist yet, so an installation that
    /// ran ToolPlus before the identity tables were introduced would silently lack all three.
    /// Creating them by hand avoids both deleting toolplus.db (losing every blueprint) and pulling
    /// in migrations.
    /// </summary>
    public void EnsureIdentityTables()
    {
        Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS ToolUser (
                Id                 TEXT    NOT NULL CONSTRAINT PK_ToolUser PRIMARY KEY,
                Username           TEXT    NOT NULL,
                PasswordHash       TEXT    NOT NULL,
                FullName           TEXT    NOT NULL,
                IsAdmin            INTEGER NOT NULL,
                IsActive           INTEGER NOT NULL,
                ExpiresAt          TEXT    NULL,
                MustChangePassword INTEGER NOT NULL,
                CreatedAt          TEXT    NOT NULL,
                CreatedBy          TEXT    NOT NULL,
                LastLoginAt        TEXT    NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ToolUser_Username ON ToolUser (Username);

            CREATE TABLE IF NOT EXISTS ToolSession (
                Id        INTEGER NOT NULL CONSTRAINT PK_ToolSession PRIMARY KEY AUTOINCREMENT,
                UserId    TEXT    NOT NULL,
                TokenHash TEXT    NOT NULL,
                Ip        TEXT    NULL,
                DeviceId  TEXT    NULL,
                UserAgent TEXT    NULL,
                CreatedAt TEXT    NOT NULL,
                ExpiresAt TEXT    NOT NULL,
                LastSeen  TEXT    NOT NULL,
                Revoked   INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ToolSession_TokenHash ON ToolSession (TokenHash);
            CREATE INDEX IF NOT EXISTS IX_ToolSession_UserId ON ToolSession (UserId);

            CREATE TABLE IF NOT EXISTS ToolUserModule (
                Id        INTEGER NOT NULL CONSTRAINT PK_ToolUserModule PRIMARY KEY AUTOINCREMENT,
                UserId    TEXT    NOT NULL,
                ModuleKey TEXT    NOT NULL,
                CanEdit   INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ToolUserModule_UserId_ModuleKey
                ON ToolUserModule (UserId, ModuleKey);
            """);
    }
}

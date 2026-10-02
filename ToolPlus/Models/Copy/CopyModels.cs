using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using ToolPlus.Models.Explorer;

namespace ToolPlus.Models.Copy
{
    /// <summary>
    /// How a row differs between the two databases, paired by primary key. Serialised by name: the
    /// frontend keys its labels and colours by these strings.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum CopyKind
    {
        /// <summary>In the source, missing here — an INSERT.</summary>
        Insert = 0,

        /// <summary>In both, at least one column differs — an UPDATE of those columns only.</summary>
        Update = 1,

        /// <summary>Here but no longer in the source — a DELETE, and only ever when asked for.</summary>
        Delete = 2
    }

    /// <summary>A copy writes in two passes, so foreign keys hold at every commit in between.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum CopyPhase
    {
        /// <summary>Deletes only, run over the tables children-first.</summary>
        Delete = 0,

        /// <summary>Inserts and updates, run over the tables parents-first.</summary>
        Upsert = 1
    }

    public class CopyTableStat
    {
        public string Full { get; set; } = "";
        public bool HasPrimaryKey { get; set; }
        public bool InSource { get; set; }

        /// <summary>From sys.partitions — free to read, and close enough to show scale. -1 = unknown.</summary>
        public long TargetRows { get; set; } = -1;
        public long SourceRows { get; set; } = -1;
    }

    public class CopyOrderRequest
    {
        public List<string> Tables { get; set; } = new();
    }

    public class CopyOrderResult
    {
        /// <summary>Parents before children, by the foreign keys of the database written to.</summary>
        public List<string> Tables { get; set; } = new();

        public List<string> Warnings { get; set; } = new();
    }

    public class CopyCompareRequest
    {
        public string SourceModule { get; set; } = "";
        public string Table { get; set; } = "";

        /// <summary>
        /// Primary key of the last source row already compared, as the grid's strings. Null = start.
        /// A step stops after <c>Copy:StepSeconds</c> and hands this back for the next one.
        /// </summary>
        public List<string?>? Cursor { get; set; }
    }

    /// <summary>One row of the comparison, in display form.</summary>
    public class CopyDiffRow
    {
        public CopyKind Kind { get; set; }

        /// <summary>Primary key -> value. The only thing the browser ever sends back to name a row.</summary>
        public Dictionary<string, string?> Key { get; set; } = new();

        public string? Label { get; set; }

        /// <summary>Update only: the columns that differ, From = here, To = the source.</summary>
        public List<ChangeLog>? Changes { get; set; }

        /// <summary>Insert: the source row. Delete: the row here. Long values are shortened.</summary>
        public Dictionary<string, string?>? Values { get; set; }
    }

    /// <summary>
    /// The result of one compare step. Counts are for THIS step only — the browser adds them up —
    /// so no step needs to remember the ones before it.
    /// </summary>
    public class CopyCompareStep
    {
        public string Table { get; set; } = "";
        public List<string> Keys { get; set; } = new();

        /// <summary>Columns compared and copied — the target's, minus computed and rowversion ones.</summary>
        public List<string> Columns { get; set; } = new();

        public string LabelColumn { get; set; } = "";

        public long SourceApprox { get; set; } = -1;
        public long TargetApprox { get; set; } = -1;

        public long SourceScanned { get; set; }
        public long TargetScanned { get; set; }

        public int Insert { get; set; }
        public int Update { get; set; }
        public int Delete { get; set; }
        public int Same { get; set; }

        /// <summary>At most <c>Copy:MaxListedRows</c> of each kind.</summary>
        public List<CopyDiffRow> Rows { get; set; } = new();

        /// <summary>Where the next step starts. Null = the table has been compared to the end.</summary>
        public List<string?>? Cursor { get; set; }

        /// <summary>Writing is possible at all: the module is a copy target and the account may edit.</summary>
        public bool CanApply { get; set; }

        public List<string> Warnings { get; set; } = new();

        public long ElapsedMs { get; set; }
    }

    public class CopyApplyRequest
    {
        public string SourceModule { get; set; } = "";
        public string Table { get; set; } = "";

        /// <summary>Ties every step of one copy together in the audit log. Empty = start a new run.</summary>
        public Guid RunId { get; set; }

        public CopyPhase Phase { get; set; }

        /// <summary>Which kinds of difference this run may write. The server enforces them, not the UI.</summary>
        public bool Insert { get; set; }
        public bool Update { get; set; }
        public bool Delete { get; set; }

        /// <summary>
        /// Rows ticked by hand. Null = every row of the allowed kinds, found by scanning the whole
        /// table again. Either way the values to write are read from the source on the server — the
        /// browser only ever names rows, never their content.
        /// </summary>
        public List<Dictionary<string, string?>>? Keys { get; set; }

        /// <summary>Full-scan mode only: where to resume, exactly as the previous step returned it.</summary>
        public List<string?>? Cursor { get; set; }
    }

    public class CopyApplyStep
    {
        public Guid RunId { get; set; }

        public int Inserted { get; set; }
        public int Updated { get; set; }
        public int Deleted { get; set; }

        /// <summary>Ticked rows that no longer differ (or no longer exist on either side).</summary>
        public int Skipped { get; set; }

        public long SourceScanned { get; set; }

        /// <summary>Null = this table and phase are finished.</summary>
        public List<string?>? Cursor { get; set; }

        public long ElapsedMs { get; set; }
    }
}

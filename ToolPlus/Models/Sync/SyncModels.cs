using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using ToolPlus.Models.Explorer;

namespace ToolPlus.Models.Sync
{
    /// <summary>
    /// What the run is allowed to do. This is the safety switch of the whole feature: it decides
    /// both which target rows are even fetched and which ones ApplyAsync will write.
    /// </summary>
    public enum SyncMode
    {
        /// <summary>Only rows whose target value is empty. Nothing that already holds data is touched.</summary>
        FillEmpty = 0,

        /// <summary>Empty rows and rows that differ. The only mode that can overwrite real data.</summary>
        FillAndOverwrite = 1,

        /// <summary>Report the differences and refuse to write anything at all.</summary>
        CompareOnly = 2
    }

    /// <summary>
    /// What a single compared row turned out to be. The first two are the only states that ever
    /// produce an UPDATE; the rest exist so the user can see WHY a row was left alone.
    /// </summary>
    public enum SyncRowState
    {
        /// <summary>Target empty, source has a value — the ordinary "repair a wiped column" case.</summary>
        Fill = 0,

        /// <summary>Both sides hold a value and they differ.</summary>
        Overwrite = 1,

        Same = 2,

        /// <summary>The source is empty too, so there is nothing to recover from.</summary>
        SourceEmpty = 3,

        /// <summary>No row on the source side carries this match value.</summary>
        NotFound = 4,

        /// <summary>Several source rows share the match value but disagree — never guess which is right.</summary>
        Ambiguous = 5,

        /// <summary>The source value cannot physically go into the target column (too long, wrong type…).</summary>
        Blocked = 6,

        /// <summary>Written. Only ever set by ApplyAsync.</summary>
        Updated = 7,

        /// <summary>The row changed between the preview and the write, so it was left alone.</summary>
        Stale = 8
    }

    /// <summary>
    /// One run's configuration. Apply re-sends exactly this, so the server can rebuild the plan from
    /// scratch instead of trusting values that have been through a browser.
    /// </summary>
    public class SyncPlanRequest
    {
        /// <summary>Module to read from. The module written to is always the one in the URL.</summary>
        public string SourceModule { get; set; } = "";

        public string TargetTable { get; set; } = "";

        /// <summary>Empty = the same table name as the target.</summary>
        public string SourceTable { get; set; } = "";

        /// <summary>Column that pairs a target row with a source row — a business key, not necessarily the PK.</summary>
        public string MatchColumn { get; set; } = "";

        /// <summary>Empty = the same column name as <see cref="MatchColumn"/>.</summary>
        public string SourceMatchColumn { get; set; } = "";

        /// <summary>The column being repaired.</summary>
        public string Column { get; set; } = "";

        /// <summary>Empty = the same column name as <see cref="Column"/>.</summary>
        public string SourceColumn { get; set; } = "";

        /// <summary>
        /// Column that says WHICH row a line refers to — a username, an employee code. Shown in the
        /// comparison and recorded in the audit log, because a primary key here is usually a GUID.
        /// Empty = let the server pick one.
        /// </summary>
        public string LabelColumn { get; set; } = "";

        public SyncMode Mode { get; set; } = SyncMode.FillEmpty;

        /// <summary>Extra conditions on the TARGET table, to cut a big table down to the part being fixed.</summary>
        public List<FilterItem> Filters { get; set; } = new();

        /// <summary>0 = the configured cap. Never allowed above it.</summary>
        public int Take { get; set; }
    }

    public class SyncApplyRequest : SyncPlanRequest
    {
        /// <summary>
        /// Primary keys of the rows the user ticked — and nothing else. The value to write is looked
        /// up again on the server: a browser must not be able to name both the row and its new
        /// content, and the source may have moved since the preview was drawn.
        /// </summary>
        public List<Dictionary<string, string?>> Keys { get; set; } = new();
    }

    public class SyncRow
    {
        /// <summary>Target primary key -> value, so the UPDATE can only ever hit one row.</summary>
        public Dictionary<string, string?> Key { get; set; } = new();

        /// <summary>Value of the match column, shown in the grid so the row is recognisable.</summary>
        public string? Match { get; set; }

        /// <summary>Value of the label column — who or what this row is, in readable form.</summary>
        public string? Label { get; set; }

        /// <summary>What the target holds right now.</summary>
        public string? Current { get; set; }

        /// <summary>What the source offers. Null when there is nothing to take.</summary>
        public string? Incoming { get; set; }

        /// <summary>
        /// Serialised by name, not by number: the frontend switches on it and keys its labels and
        /// colours by these strings, so reordering the enum here must not quietly change what the
        /// browser is told.
        /// </summary>
        [JsonConverter(typeof(StringEnumConverter))]
        public SyncRowState State { get; set; }

        /// <summary>Why this row is blocked, ambiguous or stale — empty when the state says enough.</summary>
        public string? Note { get; set; }
    }

    public class SyncPlanResult
    {
        public string SourceModule { get; set; } = "";
        public string SourceModuleName { get; set; } = "";
        public string SourceTable { get; set; } = "";
        public string TargetTable { get; set; } = "";
        public string MatchColumn { get; set; } = "";
        public string SourceMatchColumn { get; set; } = "";
        public string Column { get; set; } = "";
        public string SourceColumn { get; set; } = "";

        /// <summary>The label column actually used — the server's pick when the request named none.</summary>
        public string LabelColumn { get; set; } = "";

        public List<SyncRow> Rows { get; set; } = new();

        /// <summary>Candidate rows read from the target.</summary>
        public int Scanned { get; set; }

        /// <summary>The cap was reached — there is more to fix than what is on screen.</summary>
        public bool Truncated { get; set; }

        public string TargetSql { get; set; } = "";
        public string SourceSql { get; set; } = "";

        /// <summary>Writing is possible at all: the mode allows it and so do the edit permissions.</summary>
        public bool CanApply { get; set; }

        public List<string> Warnings { get; set; } = new();

        public long ElapsedMs { get; set; }
    }

    public class SyncApplyResult
    {
        /// <summary>Ties this run's audit rows together — the handle an undo script is built from.</summary>
        public Guid RunId { get; set; }

        public int Updated { get; set; }

        /// <summary>Ticked but not written — the row had moved on, or was never eligible.</summary>
        public int Skipped { get; set; }

        /// <summary>Outcome per row, in the order they were attempted.</summary>
        public List<SyncRow> Rows { get; set; } = new();

        /// <summary>One representative statement, shown so the user can see what actually ran.</summary>
        public string Sql { get; set; } = "";

        public long ElapsedMs { get; set; }
    }
}

namespace ToolPlus.Models.Explorer
{
    public class TableInfo
    {
        public string Schema { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsView { get; set; }

        /// <summary>As schema.name — the key every client request uses.</summary>
        public string Full => Schema + "." + Name;

        /// <summary>As [schema].[name], ready to be put into SQL.</summary>
        public string Quoted => "[" + Schema + "].[" + Name + "]";
    }

    public class ColumnInfo
    {
        public string Name { get; set; } = "";
        public string DataType { get; set; } = "";
        public bool IsNullable { get; set; }
        public bool IsPrimaryKey { get; set; }

        /// <summary>identity / computed / rowversion — shown, but not editable.</summary>
        public bool IsReadOnly { get; set; }

        /// <summary>Binary or spatial type — never SELECTed, as it means nothing in a grid.</summary>
        public bool IsSkipped { get; set; }

        /// <summary>nvarchar(max)/text… — hidden by default to keep the payload small.</summary>
        public bool IsLong { get; set; }

        /// <summary>A string type — eligible for keyword search.</summary>
        public bool IsText { get; set; }

        /// <summary>An integer type — a numeric keyword is compared for equality directly.</summary>
        public bool IsNumber { get; set; }

        public bool IsGuid { get; set; }
        public int MaxLength { get; set; }
    }

    public class FilterItem
    {
        public string Column { get; set; } = "";

        /// <summary>= &lt;&gt; &gt; &gt;= &lt; &lt;= like startswith in null notnull</summary>
        public string Op { get; set; } = "=";

        public string? Value { get; set; }
    }

    public class SearchRequest
    {
        public string Table { get; set; } = "";
        public string? Keyword { get; set; }

        /// <summary>Accent-insensitive search (COLLATE …_CI_AI).</summary>
        public bool BoDau { get; set; } = true;

        public List<FilterItem> Filters { get; set; } = new();
        public string? SortColumn { get; set; }
        public bool SortDesc { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 50;

        /// <summary>Count the total rows — turn off on very large tables to keep it fast.</summary>
        public bool CountTotal { get; set; } = true;

        public bool IncludeLongText { get; set; }
    }

    public class GridResult
    {
        public List<ColumnInfo> Columns { get; set; } = new();
        public List<Dictionary<string, object?>> Rows { get; set; } = new();

        /// <summary>-1 = not counted, or counting failed.</summary>
        public int Total { get; set; } = -1;

        /// <summary>The SQL that ran — shown in the UI so the user can check it themselves.</summary>
        public string Sql { get; set; } = "";

        public long ElapsedMs { get; set; }

        /// <summary>Names of the columns that were hidden (long or binary types).</summary>
        public List<string> HiddenColumns { get; set; } = new();

        public bool Truncated { get; set; }
    }

    public class UpdateRequest
    {
        public string Table { get; set; } = "";

        /// <summary>Every primary-key column -> value, so the WHERE clause can only hit one row.</summary>
        public Dictionary<string, string?> Key { get; set; } = new();

        /// <summary>Column to change -> new value. null means set NULL.</summary>
        public Dictionary<string, string?> Changes { get; set; } = new();
    }

    public class ChangeLog
    {
        public string Column { get; set; } = "";
        public string? From { get; set; }
        public string? To { get; set; }
    }

    public class QueryRequest
    {
        public string Sql { get; set; } = "";
    }

    /// <summary>One column pair of a real foreign key in the database; tables as schema.name.</summary>
    public class ForeignKeyInfo
    {
        /// <summary>The child table — the one holding the referencing column.</summary>
        public string ChildTable { get; set; } = "";
        public string ChildColumn { get; set; } = "";

        /// <summary>The parent table — the one being referenced.</summary>
        public string ParentTable { get; set; } = "";
        public string ParentColumn { get; set; } = "";
    }
}

using ToolPlus.Models.Explorer;

namespace ToolPlus.Services.Interface
{
    public interface IExplorerService
    {
        Task<GridResult> SearchAsync(SearchRequest req);

        /// <summary>Updates exactly one row by primary key. Returns the changes that were written.</summary>
        Task<List<ChangeLog>> UpdateAsync(UpdateRequest req, string username, string? ip);

        /// <summary>Runs a user-supplied SELECT (every write statement is already rejected).</summary>
        Task<GridResult> QueryAsync(string sql);
    }
}

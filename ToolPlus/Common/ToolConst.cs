namespace ToolPlus.Common
{
    /// <summary>
    /// Shared constants. The status strings are a wire contract with the frontend
    /// (see request() in wwwroot/js/site.js) — changing a value means changing both sides.
    /// </summary>
    public static class ToolConst
    {
        public static readonly string OK = "OK";
        public static readonly string Error = "Error";
        public static readonly string UserNotFound = "UsernotFound";
        public static readonly string PerrmissionDenied = "PerrmissionDenied";
        public static readonly string TokenIsNotCorrect = "TokenIsNotCorrect";

        /// <summary>Cache buster appended to css/js paths; bump it on release.</summary>
        public static readonly string JSVersion = "?ver=toolplus.20260727.01";

        /// <summary>
        /// Header carrying the session token for requests made from a script or curl instead of a
        /// browser cookie. Deliberately not called "token": a name that generic collides with
        /// other apps' headers and reads as if the tokens were interchangeable.
        /// </summary>
        public const string TokenHeader = "X-Tool-Token";
    }
}

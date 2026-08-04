using Newtonsoft.Json;
using ToolPlus.Common;

namespace ToolPlus.Models.APIModels
{
    /// <summary>
    /// Envelope every API response uses: a status string the frontend switches on, a message for
    /// the user, and an optional payload. <see cref="Data"/> is serialised eagerly so callers can
    /// hand over anonymous objects without the outer serialiser having to know their shape.
    /// </summary>
    public class ResponseModel
    {
        public string? Status { get; set; }
        public string? Message { get; set; }
        public string? Data { get; set; }

        public ResponseModel() { }

        public ResponseModel(string status, string message)
        {
            Status = status;
            Message = message;
            Data = null;
        }

        public ResponseModel(string status, string message, object data)
        {
            Status = status;
            Message = message;
            Data = JsonConvert.SerializeObject(data);
        }
    }

    public class ResponseModelSuccess : ResponseModel
    {
        public ResponseModelSuccess() => Status = ToolConst.OK;

        public ResponseModelSuccess(string message, object? data = null)
        {
            Status = ToolConst.OK;
            Message = message;
            Data = JsonConvert.SerializeObject(data);
        }
    }

    public class ResponseModelError : ResponseModel
    {
        public ResponseModelError() => Status = ToolConst.Error;

        public ResponseModelError(string message, object? data = null)
        {
            Status = ToolConst.Error;
            Message = message;
            Data = JsonConvert.SerializeObject(data);
        }
    }
}

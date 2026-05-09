namespace TqkLibrary.Asp.ToolKit.ViewModels
{
    public class ApiException : Exception
    {
        public ApiException(string? message = null) : base(message)
        {
        }
        public ApiException(IApiResult apiResult) : base(apiResult.ExceptionInfo?.ToString() ?? apiResult.Message)
        {
            this.ApiResult = apiResult;
        }
        public IApiResult? ApiResult { get; }
    }
}

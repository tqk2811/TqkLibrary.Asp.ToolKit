using System.Diagnostics.CodeAnalysis;

namespace TqkLibrary.Asp.ToolKit.ViewModels
{
    public interface IExceptionInfo
    {
        string ExceptionType { get; }
        string Message { get; }
        string StackTrace { get; }
    }
    public class ExceptionInfo : IExceptionInfo
    {
        public ExceptionInfo()
        {

        }
        [SetsRequiredMembers]
        public ExceptionInfo(Exception exception)
        {
            Type type = exception.GetType();
            this.ExceptionType = type.FullName ?? type.Name;
            this.Message = exception.Message ?? string.Empty;
            this.StackTrace = exception.StackTrace ?? string.Empty;
        }
        public required string ExceptionType { get; set; }
        public required string Message { get; set; }
        public required string StackTrace { get; set; }

        public override string ToString()
        {
            return $"{ExceptionType}: {Message}{Environment.NewLine}{StackTrace}";
        }

        public static implicit operator ExceptionInfo(Exception exception) => new(exception);
        public static ExceptionInfo? Clone(IExceptionInfo? exceptionInfo)
        {
            if (exceptionInfo is null) return null;
            return new ExceptionInfo()
            {
                ExceptionType = exceptionInfo.ExceptionType,
                Message = exceptionInfo.Message,
                StackTrace = exceptionInfo.StackTrace,
            };
        }
    }
}

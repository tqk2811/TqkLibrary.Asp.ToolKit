using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.Text;

namespace TqkLibrary.Asp.ToolKit.ViewModels
{
    public interface IApiResult
    {
        bool IsSuccessed { get; }
        string? Message { get; }
        IReadOnlyList<IValidationResult>? Validations { get; }
        IExceptionInfo? ExceptionInfo { get; }
    }
    public interface IApiResult<out T> : IApiResult
    {
        T? ResultObj { get; }
    }
    public class ApiResult : IApiResult
    {
        public bool IsSuccessed { get; set; }
        public string? Message { get; set; }
        public ExceptionInfo? ExceptionInfo { get; set; }
        public List<ValidationResult>? Validations { get; set; }

        IExceptionInfo? IApiResult.ExceptionInfo => ExceptionInfo;
        IReadOnlyList<IValidationResult>? IApiResult.Validations => Validations;
        public static implicit operator ApiResult(ModelStateDictionary modelStateDictionary) => new ApiErrorResult(modelStateDictionary);
        public static implicit operator ApiResult(bool isSuccessed) => new ApiResult() { IsSuccessed = isSuccessed };

        public string? GetErrorMsg(bool isDebug = false)
        {
            if (Validations?.Any() == true)
            {
                StringBuilder stringBuilder = new();
                foreach (var validation in Validations)
                {
                    if (validation.Messages?.Any() == true)
                    {
                        stringBuilder.AppendLine(validation.Name + ":");
                        foreach (var message in validation.Messages)
                        {
                            stringBuilder.AppendLine("\t" + message);
                        }
                        stringBuilder.AppendLine();
                    }
                }
                return stringBuilder.ToString();
            }
            else if (isDebug && ExceptionInfo is not null)
            {
                return $"{ExceptionInfo.ExceptionType}: {ExceptionInfo.Message}{Environment.NewLine}{ExceptionInfo.StackTrace}";
            }
            return Message;
        }
    }
    public class ApiSuccessResult : ApiResult
    {
        public ApiSuccessResult(string? message = null)
        {
            this.IsSuccessed = true;
            this.Message = message ?? string.Empty;
        }
    }
    public class ApiErrorResult : ApiResult
    {
        public ApiErrorResult(string? message = null)
        {
            this.IsSuccessed = false;
            this.Message = message;
        }
        public ApiErrorResult(ExceptionInfo exceptionInfo, string? message = null)
        {
            this.IsSuccessed = false;
            this.ExceptionInfo = exceptionInfo;
            this.Message = message;
        }
        public ApiErrorResult(IEnumerable<ValidationResult> validations, string? message = null)
        {
            this.IsSuccessed = false;
            this.Validations = validations?.ToList();
            this.Message = message;
        }
        public ApiErrorResult(ModelStateDictionary modelStateDictionary, string? message = null)
        {
            if (modelStateDictionary.IsValid) throw new InvalidOperationException();
            this.IsSuccessed = modelStateDictionary.IsValid;
            this.Validations = modelStateDictionary?.Parse()?.ToList();
            this.Message = message;
        }

        public static implicit operator ApiErrorResult(ModelStateDictionary modelStateDictionary) => new(modelStateDictionary);
    }
    public class ApiResult<T> : ApiResult, IApiResult<T>
    {
        public T? ResultObj { get; set; }


        public static implicit operator ApiResult<T>(T? resultObj)
        {
            if (resultObj is null) return new ApiErrorResult<T>();
            else return new ApiSuccessResult<T>(resultObj);
        }
        public static implicit operator ApiResult<T>(Tuple<T, string?> tuple) => new ApiSuccessResult<T>(tuple.Item1, tuple.Item2);
        public static implicit operator ApiResult<T>(Tuple<string?, T> tuple) => new ApiSuccessResult<T>(tuple.Item2, tuple.Item1);
        public static implicit operator ApiResult<T>(ValueTuple<T, string?> tuple) => new ApiSuccessResult<T>(tuple.Item1, tuple.Item2);
        public static implicit operator ApiResult<T>(ValueTuple<string?, T> tuple) => new ApiSuccessResult<T>(tuple.Item2, tuple.Item1);
        public static implicit operator ApiResult<T>(ModelStateDictionary modelStateDictionary) => new ApiErrorResult<T>(modelStateDictionary);
        public static implicit operator ApiResult<T>(ApiSuccessResult apiSuccessResult) => new ApiResult<T>()
        {
            IsSuccessed = apiSuccessResult.IsSuccessed,
            Validations = apiSuccessResult.Validations,
            ExceptionInfo = apiSuccessResult.ExceptionInfo,
            Message = apiSuccessResult.Message,
            ResultObj = default,
        };
        public static implicit operator ApiResult<T>(ApiErrorResult apiErrorResult) => new ApiResult<T>()
        {
            IsSuccessed = apiErrorResult.IsSuccessed,
            Validations = apiErrorResult.Validations,
            ExceptionInfo = apiErrorResult.ExceptionInfo,
            Message = apiErrorResult.Message,
            ResultObj = default,
        };
        public static implicit operator ApiResult<T>(bool isSuccessed) => new ApiResult<T>() { IsSuccessed = isSuccessed };

        public ApiResult<TBase> As<TBase>() where TBase : class
        {
            return new ApiResult<TBase>
            {
                IsSuccessed = IsSuccessed,
                Message = Message,
                ExceptionInfo = ExceptionInfo,
                Validations = Validations,
                ResultObj = ResultObj as TBase,
            };
        }

        public ApiResult<TDest> Map<TDest>(Func<T, TDest> mapper)
        {
            return new ApiResult<TDest>
            {
                IsSuccessed = IsSuccessed,
                Message = Message,
                ExceptionInfo = ExceptionInfo,
                Validations = Validations,
                ResultObj = ResultObj is not null ? mapper(ResultObj) : default,
            };
        }
    }
    public class ApiSuccessResult<T> : ApiResult<T>
    {
        public ApiSuccessResult()
        {
            this.IsSuccessed = true;
        }
        public ApiSuccessResult(T resultObj, string? message = null)
        {
            this.IsSuccessed = true;
            this.ResultObj = resultObj;
            this.Message = message;
        }
    }
    public class ApiErrorResult<T> : ApiResult<T>
    {
        public ApiErrorResult(T resultObj, string? message = null)
        {
            this.IsSuccessed = false;
            this.ResultObj = resultObj;
            this.Message = message;
        }
        public ApiErrorResult(string? message = null)
        {
            this.IsSuccessed = false;
            this.Message = message;
        }
        public ApiErrorResult(ExceptionInfo exceptionInfo, string? message = null)
        {
            this.IsSuccessed = false;
            this.ExceptionInfo = exceptionInfo;
            this.Message = message;
        }
        public ApiErrorResult(IEnumerable<ValidationResult> validations, string? message = null)
        {
            this.IsSuccessed = false;
            this.Validations = validations?.ToList();
            this.Message = message;
        }
        public ApiErrorResult(ModelStateDictionary modelStateDictionary, string? message = null)
        {
            if (modelStateDictionary.IsValid) throw new InvalidOperationException();
            this.IsSuccessed = modelStateDictionary.IsValid;
            this.Validations = modelStateDictionary?.Parse()?.ToList();
            this.Message = message;
        }

        public static implicit operator ApiErrorResult<T>(ApiErrorResult apiErrorResult) => new()
        {
            IsSuccessed = apiErrorResult.IsSuccessed,
            ExceptionInfo = apiErrorResult.ExceptionInfo,
            Message = apiErrorResult.Message,
            ResultObj = default,
            Validations = apiErrorResult.Validations,
        };
        public static implicit operator ApiErrorResult<T>(ModelStateDictionary modelStateDictionary) => new(modelStateDictionary);
    }
}

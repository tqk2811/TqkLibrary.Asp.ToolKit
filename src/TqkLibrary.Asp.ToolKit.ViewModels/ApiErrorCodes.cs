namespace TqkLibrary.Asp.ToolKit.ViewModels
{
    /// <summary>Generic error codes for <see cref="ApiError.Code"/> and <see cref="FieldError.Code"/>.</summary>
    public static class ApiErrorCodes
    {
        public const string ValidationFailed = "VALIDATION_FAILED";
        public const string InternalError = "INTERNAL_ERROR";
        public const string Unauthorized = "UNAUTHORIZED";
        public const string Forbidden = "FORBIDDEN";
        public const string NotFound = "NOT_FOUND";
        public const string Conflict = "CONFLICT";

        public const string FieldRequired = "FIELD_REQUIRED";
        public const string FieldInvalidValue = "FIELD_INVALID_VALUE";
        public const string FieldInvalidFormat = "FIELD_INVALID_FORMAT";
        public const string FieldOutOfRange = "FIELD_OUT_OF_RANGE";
        public const string FieldTooLong = "FIELD_TOO_LONG";
        public const string FieldTooShort = "FIELD_TOO_SHORT";
    }
}

namespace ImagingPipeline.Common.Dtos.Rules.Responses;

public enum RuleOperationStatus
{
    Success,
    PartialSuccess,
    AllFailed,
    ValidationFailed,
    NotFound,
    Conflict
}

public sealed class RuleOperationResult<T>
{
    private RuleOperationResult(RuleOperationStatus status, T? value, string? error)
    {
        Status = status;
        Value = value;
        Error = error;
    }

    public RuleOperationStatus Status { get; }
    public T? Value { get; }
    public string? Error { get; }

    public static RuleOperationResult<T> Success(T value) => new(RuleOperationStatus.Success, value, null);
    public static RuleOperationResult<T> PartialSuccess(T value) => new(RuleOperationStatus.PartialSuccess, value, null);
    public static RuleOperationResult<T> AllFailed(T value) => new(RuleOperationStatus.AllFailed, value, null);
    public static RuleOperationResult<T> ValidationFailed(string error) => new(RuleOperationStatus.ValidationFailed, default, error);
    public static RuleOperationResult<T> NotFound(string error) => new(RuleOperationStatus.NotFound, default, error);
    public static RuleOperationResult<T> Conflict(string error) => new(RuleOperationStatus.Conflict, default, error);
}

public sealed class RuleOperationResult
{
    private RuleOperationResult(RuleOperationStatus status, string? error)
    {
        Status = status;
        Error = error;
    }

    public RuleOperationStatus Status { get; }
    public string? Error { get; }

    public static RuleOperationResult Success() => new(RuleOperationStatus.Success, null);
    public static RuleOperationResult ValidationFailed(string error) => new(RuleOperationStatus.ValidationFailed, error);
    public static RuleOperationResult NotFound(string error) => new(RuleOperationStatus.NotFound, error);
    public static RuleOperationResult Conflict(string error) => new(RuleOperationStatus.Conflict, error);
}

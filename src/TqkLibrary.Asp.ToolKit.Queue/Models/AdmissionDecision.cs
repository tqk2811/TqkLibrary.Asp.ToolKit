using TqkLibrary.Asp.ToolKit.Queue.Enums;

namespace TqkLibrary.Asp.ToolKit.Queue.Models;

/// <summary>
/// Quyết định của <see cref="Interfaces.IAdmissionPolicy{TItem}"/> cho một việc đang chờ.
/// <see cref="RetryAfter"/> chỉ có nghĩa khi <see cref="Kind"/> là <see cref="AdmissionKind.Delay"/>.
/// </summary>
public readonly record struct AdmissionDecision(AdmissionKind Kind, TimeSpan RetryAfter)
{
    public static AdmissionDecision Start { get; } = new(AdmissionKind.Start, TimeSpan.Zero);

    public static AdmissionDecision Wait { get; } = new(AdmissionKind.Wait, TimeSpan.Zero);

    public static AdmissionDecision Delay(TimeSpan retryAfter)
        => new(AdmissionKind.Delay, retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter);
}

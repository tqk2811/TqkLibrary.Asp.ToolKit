using TqkLibrary.Asp.ToolKit.Queue.Enums;

namespace TqkLibrary.Asp.ToolKit.Queue.Models;

/// <summary>
/// Decision of <see cref="Interfaces.IAdmissionPolicy{TItem}"/> for a waiting item.
/// <see cref="RetryAfter"/> is meaningful only when <see cref="Kind"/> is <see cref="AdmissionKind.Delay"/>.
/// </summary>
public readonly record struct AdmissionDecision(AdmissionKind Kind, TimeSpan RetryAfter)
{
    public static AdmissionDecision Start { get; } = new(AdmissionKind.Start, TimeSpan.Zero);

    public static AdmissionDecision Wait { get; } = new(AdmissionKind.Wait, TimeSpan.Zero);

    public static AdmissionDecision Delay(TimeSpan retryAfter)
        => new(AdmissionKind.Delay, retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter);
}

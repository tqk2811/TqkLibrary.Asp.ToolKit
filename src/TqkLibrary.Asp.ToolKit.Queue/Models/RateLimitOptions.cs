namespace TqkLibrary.Asp.ToolKit.Queue.Models;

/// <summary>
/// Configuration of <see cref="Policies.RateLimitPolicy{TItem}"/>: paces the START of work (does not limit how many items run at once,
/// that is the job of <see cref="Policies.ConcurrencyCapPolicy{TItem}"/>). The two limits can be used alone or together.
/// </summary>
public sealed class RateLimitOptions
{
    /// <summary>Minimum interval between two starts. <see cref="TimeSpan.Zero"/> = disabled.</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.Zero;

    /// <summary>Maximum number of starts in any sliding window of length <see cref="Window"/>. <c>null</c> = disabled.</summary>
    public int? MaxPerWindow { get; init; }

    /// <summary>Length of the sliding window of <see cref="MaxPerWindow"/>; must be positive when <see cref="MaxPerWindow"/> has a value.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// <c>true</c> (default): priority work always starts, ignoring the limits, but is still recorded in the last-start mark/window
    /// so normal work afterwards yields. <c>false</c>: priority work is limited like normal work.
    /// </summary>
    public bool PriorityBypass { get; init; } = true;
}

namespace TqkLibrary.Asp.ToolKit.Queue.Models;

/// <summary>
/// A concurrency cap the work must pass. Normal work may start when the number of running items with the same <see cref="Key"/>
/// is less than <see cref="Max"/>; priority work gets <see cref="PriorityExtra"/> additional slots that normal work never uses.
/// </summary>
/// <param name="Key">Counter key, e.g. <c>("customer", CustomerId)</c> or <c>("provider", provider)</c>. Compared with <see cref="object.Equals(object)"/>.</param>
/// <param name="Max">Maximum number of normal items running at once with the same key.</param>
/// <param name="PriorityExtra">Number of extra slots reserved for priority work only.</param>
public readonly record struct CapRule(object Key, int Max, int PriorityExtra = 0)
{
    public int LimitFor(bool isPriority) => isPriority ? Max + PriorityExtra : Max;
}

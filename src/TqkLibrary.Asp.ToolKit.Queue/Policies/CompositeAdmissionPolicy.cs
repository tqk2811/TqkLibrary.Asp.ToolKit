using TqkLibrary.Asp.ToolKit.Queue.Enums;
using TqkLibrary.Asp.ToolKit.Queue.Interfaces;
using TqkLibrary.Asp.ToolKit.Queue.Models;

namespace TqkLibrary.Asp.ToolKit.Queue.Policies;

/// <summary>
/// Combines several policies: work starts only when ALL policies allow it. When blocked, if any policy says
/// <c>Wait</c> (wait for an event) the result is <c>Wait</c>; if every blocking policy returns <c>Delay</c>, the longest
/// delay is used, since the work must wait until the longest-blocking policy opens.
/// </summary>
public sealed class CompositeAdmissionPolicy<TItem> : IAdmissionPolicy<TItem>
{
    private readonly IAdmissionPolicy<TItem>[] _policies;

    public CompositeAdmissionPolicy(params IAdmissionPolicy<TItem>[] policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        _policies = policies;
    }

    public AdmissionDecision CanStart(TItem item, bool isPriority)
    {
        var waitForEvent = false;
        TimeSpan? longestDelay = null;
        foreach (var policy in _policies)
        {
            var decision = policy.CanStart(item, isPriority);
            switch (decision.Kind)
            {
                case AdmissionKind.Wait:
                    waitForEvent = true;
                    break;
                case AdmissionKind.Delay:
                    if (longestDelay is null || decision.RetryAfter > longestDelay)
                        longestDelay = decision.RetryAfter;
                    break;
            }
        }

        if (waitForEvent)
            return AdmissionDecision.Wait;
        return longestDelay is { } delay ? AdmissionDecision.Delay(delay) : AdmissionDecision.Start;
    }

    public object? OnStarted(TItem item, bool isPriority)
    {
        var receipts = new object?[_policies.Length];
        for (var i = 0; i < _policies.Length; i++)
        {
            try
            {
                receipts[i] = _policies[i].OnStarted(item, isPriority);
            }
            catch
            {
                // Keep the atomic contract: if a later policy throws, release the earlier policies that already held.
                for (var j = i - 1; j >= 0; j--)
                {
                    try
                    {
                        _policies[j].OnFinished(item, isPriority, receipts[j]);
                    }
                    catch
                    {
                        // The original error matters more; an error during rollback must not mask it.
                    }
                }

                throw;
            }
        }

        return receipts;
    }

    public void OnFinished(TItem item, bool isPriority, object? receipt)
    {
        // The receipt is always the array created by OnStarted above; anything else means the caller broke the contract.
        var receipts = receipt as object?[];

        // A throwing policy must not make later policies miss their release.
        List<Exception>? errors = null;
        for (var i = 0; i < _policies.Length; i++)
        {
            try
            {
                _policies[i].OnFinished(item, isPriority, receipts?[i]);
            }
            catch (Exception ex)
            {
                (errors ??= new()).Add(ex);
            }
        }

        if (errors is not null)
            throw new AggregateException(errors);
    }
}

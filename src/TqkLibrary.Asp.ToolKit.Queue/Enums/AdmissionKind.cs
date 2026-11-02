namespace TqkLibrary.Asp.ToolKit.Queue.Enums;

/// <summary>Result of evaluating whether a waiting item may start now.</summary>
public enum AdmissionKind
{
    /// <summary>Start now.</summary>
    Start,

    /// <summary>Cannot start yet; re-evaluated when other work finishes or new work enters the queue.</summary>
    Wait,

    /// <summary>Cannot start yet for a time-based reason (pacing, pause after a 429); re-evaluated after a delay.</summary>
    Delay,
}

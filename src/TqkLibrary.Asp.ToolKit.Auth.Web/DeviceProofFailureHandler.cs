using TqkLibrary.Asp.ToolKit.Auth.Web.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// Decides what happens to a request of a device-bound session (DBSC) whose proof failed. The single extension point
    /// of the failure handling: <c>AddTqkWebAuth</c> registers this default as a SINGLETON (TryAdd); an app replaces it with
    /// a subclass, registered before or after <c>AddTqkWebAuth</c>:
    /// <code>services.AddSingleton&lt;DeviceProofFailureHandler, MyProofFailureHandler&gt;();</code>
    /// It is resolved from the root provider, so a subclass that needs scoped services takes them from
    /// <c>context.HttpContext.RequestServices</c>.
    /// <para>
    /// The default returns <see cref="DeviceProofFailureContext.DefaultMode"/> as an outcome. That mode is computed by the
    /// library from <see cref="DeviceBoundSessionOptions.FailureMode"/>, <see cref="DeviceBoundSessionOptions.AllowUserChoice"/>
    /// and <see cref="DeviceBoundSessionOptions.AllowedUserModes"/>: the fixed mode, or (user choice) the user's allowed
    /// <see cref="CurrentUserInfo.DeviceBindingMode"/>, falling back to <see cref="DeviceBoundSessionOptions.FailureMode"/>.
    /// </para>
    /// <para>
    /// A subclass may return any <see cref="DeviceProofOutcome"/>, call <c>base.HandleAsync</c>, or write its own
    /// response and return <see cref="DeviceProofOutcome.Handled"/>. An exception (other than a cancellation of the aborted
    /// request) or an undefined value is logged and the default outcome applies.
    /// </para>
    /// </summary>
    public class DeviceProofFailureHandler
    {
        /// <summary>The outcome for the failed proof described by <paramref name="context"/>.</summary>
        public virtual ValueTask<DeviceProofOutcome> HandleAsync(DeviceProofFailureContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return ValueTask.FromResult(ToOutcome(context.DefaultMode));
        }

        /// <summary>The outcome matching a configured mode (<c>ReportOnly</c> → <see cref="DeviceProofOutcome.Continue"/>).</summary>
        public static DeviceProofOutcome ToOutcome(DeviceProofFailureMode mode) => mode switch
        {
            DeviceProofFailureMode.ReportOnly => DeviceProofOutcome.Continue,
            DeviceProofFailureMode.SignOut => DeviceProofOutcome.SignOut,
            _ => DeviceProofOutcome.Reject,
        };
    }
}

using System.Diagnostics.Metrics;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// DBSC metrics (singleton): counter <see cref="ProofFailuresName"/> on meter <see cref="MeterName"/>, tags
    /// <c>reason</c> (<c>missing</c>, <c>expired</c>, <c>wrong_session</c>, <c>invalid</c>) and <c>mode</c> (the outcome:
    /// <c>report_only</c>, <c>reject</c>, <c>sign_out</c>, <c>handled</c>).
    /// </summary>
    internal sealed class DeviceBoundMetrics
    {
        public const string MeterName = "TqkLibrary.Asp.ToolKit.Auth.Web";
        public const string ProofFailuresName = "tqk.auth.dbsc.proof_failures";

        private readonly Counter<long> _proofFailures;

        public DeviceBoundMetrics(IMeterFactory meterFactory)
        {
            var meter = meterFactory.Create(MeterName);
            _proofFailures = meter.CreateCounter<long>(ProofFailuresName, unit: "{request}",
                description: "Requests of a device-bound session whose DBSC proof failed.");
        }

        public void ProofFailure(string reason, DeviceProofOutcome outcome)
            => _proofFailures.Add(1, new KeyValuePair<string, object?>("reason", reason), new KeyValuePair<string, object?>("mode", ModeTag(outcome)));

        public static string ReasonTag(DeviceProofFailureReason reason) => reason switch
        {
            DeviceProofFailureReason.Missing => "missing",
            DeviceProofFailureReason.Expired => "expired",
            DeviceProofFailureReason.WrongSession => "wrong_session",
            _ => "invalid",
        };

        public static string ModeTag(DeviceProofOutcome outcome) => outcome switch
        {
            DeviceProofOutcome.Continue => "report_only",
            DeviceProofOutcome.SignOut => "sign_out",
            DeviceProofOutcome.Handled => "handled",
            _ => "reject",
        };
    }
}

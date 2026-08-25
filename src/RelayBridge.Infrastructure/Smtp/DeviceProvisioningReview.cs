// SPDX-License-Identifier: MPL-2.0

using RelayBridge.Infrastructure.Microsoft;
using RelayBridge.Core.Microsoft;

namespace RelayBridge.Infrastructure.Smtp;

public sealed record DeviceProvisioningReview(
    string? Sender,
    string? ConfigurationFingerprint,
    string ConfiguredAddress,
    int Port,
    bool IsLanReachable,
    bool IsAuthenticatedSmtpAvailable,
    MicrosoftRuntimeReadiness MicrosoftReadiness,
    IReadOnlyList<string> CandidateAddresses)
{
    public static DeviceProvisioningReview Capture(
        ActiveMicrosoftConfiguration? activeConfiguration,
        DeviceEndpointAdvice advice,
        MicrosoftRuntimeReadiness microsoftReadiness)
    {
        return new DeviceProvisioningReview(
            activeConfiguration?.AuthorizedSender,
            activeConfiguration?.Fingerprint,
            advice.ConfiguredAddress,
            advice.Port,
            advice.IsLanReachable,
            advice.IsAuthenticatedSmtpAvailable,
            microsoftReadiness,
            advice.Candidates.Select(candidate => candidate.Address.ToString()).Order(StringComparer.Ordinal).ToArray());
    }

    public bool MateriallyMatches(DeviceProvisioningReview current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return string.Equals(Sender, current.Sender, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ConfigurationFingerprint, current.ConfigurationFingerprint, StringComparison.Ordinal) &&
            string.Equals(ConfiguredAddress, current.ConfiguredAddress, StringComparison.OrdinalIgnoreCase) &&
            Port == current.Port &&
            IsLanReachable == current.IsLanReachable &&
            IsAuthenticatedSmtpAvailable == current.IsAuthenticatedSmtpAvailable &&
            MicrosoftReadiness == current.MicrosoftReadiness &&
            CandidateAddresses.SequenceEqual(current.CandidateAddresses, StringComparer.Ordinal);
    }
}

// SPDX-License-Identifier: MPL-2.0

namespace RelayBridge.Infrastructure.Microsoft;

public sealed class MicrosoftIdentityOptions
{
    public int CertificateExpiryWarningDays { get; set; } = 60;

    public int GeneratedCertificateValidityDays { get; set; } = 365;

    public TimeSpan AuthenticationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public void Validate()
    {
        if (CertificateExpiryWarningDays is < 1 or > 365)
        {
            throw new InvalidOperationException("The certificate expiry warning must be between 1 and 365 days.");
        }

        if (GeneratedCertificateValidityDays is < 30 or > 825)
        {
            throw new InvalidOperationException("Generated certificate validity must be between 30 and 825 days.");
        }

        if (AuthenticationTimeout < TimeSpan.FromSeconds(5) || AuthenticationTimeout > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException("The Microsoft authentication timeout must be between 5 seconds and 5 minutes.");
        }
    }
}

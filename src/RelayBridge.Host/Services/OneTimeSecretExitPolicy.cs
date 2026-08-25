// SPDX-License-Identifier: MPL-2.0

namespace RelayBridge.Host.Services;

public static class OneTimeSecretExitPolicy
{
    public static bool CanLeave(string? plaintextSecret, bool savedAcknowledged)
    {
        return plaintextSecret is null || savedAcknowledged;
    }
}

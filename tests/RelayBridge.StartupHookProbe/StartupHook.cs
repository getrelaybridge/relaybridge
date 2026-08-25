// SPDX-License-Identifier: MPL-2.0

namespace RelayBridge.StartupHookProbe
{
    public sealed class ProbeMarker;

    internal static class StartupHookImplementation
    {
        internal static void WriteMarker()
        {
            var path = Environment.GetEnvironmentVariable("RELAYBRIDGE_STARTUP_HOOK_MARKER");
            if (!string.IsNullOrWhiteSpace(path))
            {
                File.WriteAllText(path, "executed-before-main");
            }
        }
    }
}

internal static class StartupHook
{
    public static void Initialize() =>
        RelayBridge.StartupHookProbe.StartupHookImplementation.WriteMarker();
}

// SPDX-License-Identifier: MPL-2.0

using System.Runtime.InteropServices;

namespace RelayBridge.Setup;

internal static partial class NativeConfirmation
{
    private const uint MbYesNo = 0x00000004;
    private const uint MbIconShield = 0x00002000;
    private const uint MbSetForeground = 0x00010000;
    private const int IdYes = 6;

    internal static bool Show(string sender, bool isRepair)
    {
        var mode = isRepair ? "Repair" : "New setup";
        var message =
            $"Windows account: {Environment.UserDomainName}\\{Environment.UserName}\n\n" +
            $"Sender: {sender}\nMode: {mode}\n\n" +
            "RelayBridge will:\n" +
            "• Create or verify the RelayBridge Microsoft application\n" +
            "• Register the public authentication certificate\n" +
            "• Configure scoped Exchange SMTP permission\n\n" +
            "RelayBridge will not receive your Microsoft password or retain administrator access.\n\n" +
            "Continue?";
        return MessageBox(IntPtr.Zero, message, "RelayBridge Microsoft 365 Setup", MbYesNo | MbIconShield | MbSetForeground) == IdYes;
    }

    internal static void ShowFailure(string message)
    {
        const uint mbOkIconError = 0x00000010;
        _ = MessageBox(IntPtr.Zero, message, "RelayBridge Microsoft 365 Setup", mbOkIconError | MbSetForeground);
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr window, string text, string caption, uint type);
}

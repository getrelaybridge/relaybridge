// SPDX-License-Identifier: MPL-2.0

using RelayBridge.Core.Microsoft;
using RelayBridge.Setup;

if (!OperatingSystem.IsWindows() || args.Length != 0)
{
    return 2;
}

var sessionEstablished = false;
try
{
    await using var input = Console.OpenStandardInput();
    await using var output = Console.OpenStandardOutput();
    var start = await NativeSetupPipeProtocol.ReadAsync<NativeSetupStartRequest>(
        input,
        CancellationToken.None).ConfigureAwait(false);
    if (start.Version != NativeMicrosoftSetupProtocol.Version || start.SessionId == Guid.Empty)
    {
        throw new InvalidDataException("RelayBridge returned an invalid setup session.");
    }

    WorkerOriginVerifier.Verify(start);

    sessionEstablished = true;

    var confirmation = NativeConfirmation.Show(start.SenderMailbox, start.IsRepair);
    if (!confirmation)
    {
        await NativeSetupPipeProtocol.WriteAsync(
            output,
            new NativeSetupEnvelope(
                NativeMicrosoftSetupProtocol.Version,
                NativeSetupMessageKind.Cancelled,
                start.SessionId,
                FailureCategory: NativeSetupFailureCategory.Cancelled),
            CancellationToken.None).ConfigureAwait(false);
        return 1;
    }

    await NativeSetupPipeProtocol.WriteAsync(
        output,
        new NativeSetupEnvelope(
            NativeMicrosoftSetupProtocol.Version,
            NativeSetupMessageKind.Confirmed,
            start.SessionId),
        CancellationToken.None).ConfigureAwait(false);

    using var operationCancellation = new CancellationTokenSource();
    var cancellationListener = SetupOrchestrator.ListenForCancellationAsync(
        input,
        start.SessionId,
        operationCancellation);
    var orchestrator = new SetupOrchestrator(output, start);
    var result = await orchestrator.RunAsync(operationCancellation.Token).ConfigureAwait(false);
    operationCancellation.Cancel();
    await SetupOrchestrator.IgnoreCancellationListenerAsync(cancellationListener).ConfigureAwait(false);
    return result ? 0 : 1;
}
catch (OperationCanceledException)
{
    return 1;
}
catch (Exception)
{
    if (sessionEstablished)
    {
        NativeConfirmation.ShowFailure(
            "RelayBridge Microsoft setup could not start. Return to the local RelayBridge page for safe details.");
    }

    return 2;
}

// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelayBridge.Core.PrinterConnectivity;

public static class PrinterConnectivityApplyProtocol
{
    public const int Version = 1;
    public const int MaximumMessageBytes = 2048;
    public const string PipeName = "RelayBridge.PrinterConnectivity.Apply.v1";
    public const string UriPrefix = "relaybridge-printer://apply/";
}

public static class PrinterConnectivityApplyPipeProtocol
{
    public static async Task WriteAsync(
        Stream stream,
        PrinterConnectivityApplyEnvelope value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(value);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            value,
            PrinterConnectivityPipeJsonContext.Default.PrinterConnectivityApplyEnvelope);
        if (payload.Length is <= 0 or > PrinterConnectivityApplyProtocol.MaximumMessageBytes)
        {
            throw new InvalidDataException("The printer-connectivity message exceeds the allowed size.");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<PrinterConnectivityApplyEnvelope> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > PrinterConnectivityApplyProtocol.MaximumMessageBytes)
        {
            throw new InvalidDataException("The printer-connectivity message size is invalid.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize(
                payload,
                PrinterConnectivityPipeJsonContext.Default.PrinterConnectivityApplyEnvelope)
                ?? throw new InvalidDataException("The printer-connectivity message is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The printer-connectivity message is malformed.", exception);
        }
    }
}

public enum PrinterConnectivityApplyMessageKind
{
    Hello,
    Apply,
    Rejected,
}

public sealed record PrinterConnectivityApplyEnvelope(
    int Version,
    PrinterConnectivityApplyMessageKind Kind,
    Guid Revision,
    int? ProcessId = null,
    int? WindowsSessionId = null,
    string? ListenAddress = null,
    int? SmtpPort = null,
    int? ManagementPort = null,
    string? SafeCode = null);

public static class PrinterConnectivityConfiguration
{
    public static string Create(string listenAddress, int port)
    {
        var address = Validate(listenAddress, port);
        return JsonSerializer.Serialize(
            new PrinterConnectivityDocument(
                new PrinterConnectivitySmtpDocument(
                    Enabled: true,
                    address.ToString(),
                    port,
                    AllowCleartextAuthentication: true),
                new PrinterConnectivityQueueDocument(Enabled: true)),
            PrinterConnectivityConfigurationJsonContext.Default.PrinterConnectivityDocument);
    }

    public static byte[] CreateUtf8(string listenAddress, int port) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(Create(listenAddress, port));

    public static IPAddress Validate(string listenAddress, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listenAddress);
        if (!IPAddress.TryParse(listenAddress, out var address) ||
            !PrivateLanAddressPolicy.IsPrivateUnicast(address))
        {
            throw new InvalidOperationException(
                "Printer connectivity requires one explicit RFC1918 IPv4 or IPv6 ULA address.");
        }

        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException("SMTP port must be between 1 and 65535.");
        }

        return address;
    }
}

internal sealed record PrinterConnectivityDocument(
    PrinterConnectivitySmtpDocument Smtp,
    PrinterConnectivityQueueDocument Queue);

internal sealed record PrinterConnectivitySmtpDocument(
    bool Enabled,
    string ListenAddress,
    int Port,
    bool AllowCleartextAuthentication);

internal sealed record PrinterConnectivityQueueDocument(bool Enabled);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PrinterConnectivityApplyEnvelope))]
internal sealed partial class PrinterConnectivityPipeJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(PrinterConnectivityDocument))]
internal sealed partial class PrinterConnectivityConfigurationJsonContext : JsonSerializerContext;

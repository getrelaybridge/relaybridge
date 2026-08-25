// SPDX-License-Identifier: MPL-2.0

using System.Text;

namespace RelayBridge.Infrastructure.Microsoft;

internal sealed record SmtpResponse(int Code, IReadOnlyList<string> Lines)
{
    public bool IsPositiveCompletion => Code is >= 200 and <= 299;

    public bool IsTransientFailure => Code is >= 400 and <= 499;

    public bool IsPermanentFailure => Code is >= 500 and <= 599;

    public string SafeSummary
    {
        get
        {
            var summary = string.Join(" | ", Lines);
            return summary.Length <= 900 ? summary : summary[..900];
        }
    }

    public string? EnhancedStatusCode
    {
        get
        {
            foreach (var line in Lines)
            {
                var text = line.Length > 4 ? line[4..].TrimStart() : string.Empty;
                var end = text.IndexOf(' ');
                var token = end < 0 ? text : text[..end];
                var parts = token.Split('.');
                if (parts.Length == 3 && parts.All(part => part.Length > 0 && part.All(char.IsAsciiDigit)))
                {
                    return token;
                }
            }

            return null;
        }
    }
}

internal sealed class SmtpProtocolException : Exception
{
    public SmtpProtocolException(string message)
        : base(message)
    {
    }
}

internal sealed class SmtpResponseReader
{
    internal const int MaximumLineLength = 2048;
    internal const int MaximumLines = 100;
    internal const int MaximumResponseBytes = 32 * 1024;

    public async Task<SmtpResponse> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var totalBytes = 0;
        int? expectedCode = null;

        while (true)
        {
            if (lines.Count >= MaximumLines)
            {
                throw new SmtpProtocolException("SMTP response exceeded the line-count limit.");
            }

            var lineBytes = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
            totalBytes = checked(totalBytes + lineBytes.Length + 2);
            if (totalBytes > MaximumResponseBytes)
            {
                throw new SmtpProtocolException("SMTP response exceeded the total-size limit.");
            }

            if (lineBytes.Length < 4 ||
                !char.IsAsciiDigit((char)lineBytes[0]) ||
                !char.IsAsciiDigit((char)lineBytes[1]) ||
                !char.IsAsciiDigit((char)lineBytes[2]) ||
                lineBytes[3] is not ((byte)' ' or (byte)'-'))
            {
                throw new SmtpProtocolException("SMTP server returned a malformed response line.");
            }

            var code = ((lineBytes[0] - (byte)'0') * 100) +
                ((lineBytes[1] - (byte)'0') * 10) +
                (lineBytes[2] - (byte)'0');
            expectedCode ??= code;
            if (code != expectedCode)
            {
                throw new SmtpProtocolException("SMTP multiline response changed status code.");
            }

            lines.Add(Sanitize(lineBytes));
            if (lineBytes[3] == (byte)' ')
            {
                return new SmtpResponse(code, lines);
            }
        }
    }

    private static async Task<byte[]> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[MaximumLineLength];
        var count = 0;
        var previousWasCr = false;
        var oneByte = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(oneByte, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("SMTP server closed the connection while sending a response.");
            }

            var value = oneByte[0];
            if (previousWasCr)
            {
                if (value != (byte)'\n')
                {
                    throw new SmtpProtocolException("SMTP response line was not terminated with CRLF.");
                }

                return bytes[..count];
            }

            if (value == (byte)'\r')
            {
                previousWasCr = true;
                continue;
            }

            if (value == (byte)'\n')
            {
                throw new SmtpProtocolException("SMTP response used a bare line-feed.");
            }

            if (count == bytes.Length)
            {
                throw new SmtpProtocolException("SMTP response line exceeded the length limit.");
            }

            bytes[count++] = value;
        }
    }

    private static string Sanitize(ReadOnlySpan<byte> bytes)
    {
        var safe = new byte[bytes.Length];
        for (var index = 0; index < bytes.Length; index++)
        {
            var value = bytes[index];
            safe[index] = value is >= 0x20 and <= 0x7e ? value : (byte)'?';
        }

        return Encoding.ASCII.GetString(safe);
    }
}

internal sealed record SmtpCapabilities(bool StartTls, bool XOAuth2, bool Size, long? MaximumSize)
{
    public static SmtpCapabilities Parse(SmtpResponse response)
    {
        var startTls = false;
        var xoauth2 = false;
        var size = false;
        long? maximumSize = null;

        foreach (var responseLine in response.Lines.Skip(1))
        {
            var value = responseLine.Length > 4 ? responseLine[4..].Trim() : string.Empty;
            if (string.Equals(value, "STARTTLS", StringComparison.OrdinalIgnoreCase))
            {
                startTls = true;
                continue;
            }

            if (value.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase))
            {
                var mechanisms = value.Replace('=', ' ')
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                xoauth2 |= mechanisms.Skip(1).Any(mechanism =>
                    string.Equals(mechanism, "XOAUTH2", StringComparison.OrdinalIgnoreCase));
                continue;
            }

            if (value.Equals("SIZE", StringComparison.OrdinalIgnoreCase))
            {
                size = true;
                continue;
            }

            if (value.StartsWith("SIZE ", StringComparison.OrdinalIgnoreCase))
            {
                size = true;
                var text = value[5..].Trim();
                if (long.TryParse(text, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
                {
                    maximumSize = parsed;
                }
            }
        }

        return new SmtpCapabilities(startTls, xoauth2, size, maximumSize);
    }
}


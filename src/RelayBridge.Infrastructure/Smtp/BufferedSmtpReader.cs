// SPDX-License-Identifier: MPL-2.0

using System.Buffers;

namespace RelayBridge.Infrastructure.Smtp;

internal sealed class BufferedSmtpReader : IDisposable
{
    private readonly Stream _stream;
    private readonly TimeSpan _idleTimeout;
    private readonly byte[] _readBuffer;
    private readonly ArrayBufferWriter<byte> _lineBuffer = new(256);
    private int _offset;
    private int _length;

    public BufferedSmtpReader(Stream stream, TimeSpan idleTimeout)
    {
        _stream = stream;
        _idleTimeout = idleTimeout;
        _readBuffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
    }

    public async ValueTask<ReadOnlyMemory<byte>?> ReadLineAsync(
        int maximumLength,
        CancellationToken cancellationToken)
    {
        _lineBuffer.Clear();
        while (true)
        {
            if (_offset == _length)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_idleTimeout);
                try
                {
                    _length = await _stream
                        .ReadAsync(_readBuffer.AsMemory(), timeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("The SMTP client was idle for too long.");
                }

                _offset = 0;
                if (_length == 0)
                {
                    return null;
                }
            }

            var available = _readBuffer.AsSpan(_offset, _length - _offset);
            var lineFeedIndex = available.IndexOf((byte)'\n');
            var count = lineFeedIndex >= 0 ? lineFeedIndex + 1 : available.Length;
            if (_lineBuffer.WrittenCount + count > maximumLength + 2)
            {
                throw new SmtpLineTooLongException();
            }

            _lineBuffer.Write(available[..count]);
            _offset += count;
            if (lineFeedIndex < 0)
            {
                continue;
            }

            var line = _lineBuffer.WrittenMemory;
            if (line.Length < 2 || line.Span[^2] != '\r')
            {
                throw new SmtpProtocolException("SMTP lines must end with CRLF.");
            }

            var content = line[..^2];
            if (content.Span.Contains((byte)'\r'))
            {
                throw new SmtpProtocolException("Bare carriage return in SMTP line.");
            }

            return content;
        }
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_readBuffer, clearArray: true);
    }
}

internal sealed class SmtpLineTooLongException : Exception;

internal sealed class SmtpProtocolException(string message) : Exception(message);

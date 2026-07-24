using System.Buffers;
using System.Text;

namespace Rag.Infrastructure.Ingestion;

internal sealed class ValidatedTextUploadStream(Stream inner, long maximumLength) : Stream
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly Decoder _decoder = StrictUtf8.GetDecoder();
    private bool _completed;
    private bool _hasVisibleContent;
    private long _characterPosition;
    private long _length;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int bytesRead = inner.Read(buffer, offset, count);
        ValidateRead(buffer.AsSpan(offset, bytesRead), bytesRead == 0);
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        int bytesRead = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        ValidateRead(buffer.Span[..bytesRead], bytesRead == 0);
        return bytesRead;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    private void ValidateRead(ReadOnlySpan<byte> bytes, bool flush)
    {
        if (_completed)
        {
            return;
        }

        _length += bytes.Length;
        if (_length > maximumLength)
        {
            throw new UploadValidationException(
                $"The file exceeds the maximum size of {maximumLength} bytes.",
                isTooLarge: true);
        }

        int maximumCharacters = Math.Max(1, StrictUtf8.GetMaxCharCount(bytes.Length));
        char[] rentedCharacters = ArrayPool<char>.Shared.Rent(maximumCharacters);
        try
        {
            _decoder.Convert(
                bytes,
                rentedCharacters,
                flush,
                out int bytesUsed,
                out int charactersUsed,
                out bool completed);
            if (bytesUsed != bytes.Length || (flush && !completed))
            {
                throw new UploadValidationException("The file is not valid UTF-8 text.");
            }

            ValidateCharacters(rentedCharacters.AsSpan(0, charactersUsed));
        }
        catch (DecoderFallbackException)
        {
            throw new UploadValidationException("The file is not valid UTF-8 text.");
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rentedCharacters, clearArray: true);
        }

        if (flush)
        {
            _completed = true;
            if (_length == 0 || !_hasVisibleContent)
            {
                throw new UploadValidationException(
                    "The file must contain non-whitespace text.");
            }
        }
    }

    private void ValidateCharacters(ReadOnlySpan<char> characters)
    {
        foreach (char character in characters)
        {
            bool isInitialBom = _characterPosition == 0 && character == '\uFEFF';
            _characterPosition++;
            if (isInitialBom)
            {
                continue;
            }

            if (char.IsControl(character) && character is not ('\r' or '\n' or '\t'))
            {
                throw new UploadValidationException(
                    "The file contains unsupported control characters.");
            }

            if (!char.IsWhiteSpace(character))
            {
                _hasVisibleContent = true;
            }
        }
    }
}

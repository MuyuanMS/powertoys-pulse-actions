using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pulse.Host;

/// <summary>Reads persisted, redacted CLI streams without depending on live worker pipes.</summary>
public static class RunLogs
{
    public const int MaximumReadBytes = 128 * 1024;
    private const int MaximumEncodedTextBytes = 256 * 1024 - 1024;
    private const long MaximumLogBytes = 8 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static JsonObject Read(Store store, string runId, string stream, long cursor = 0, int limitBytes = 65536)
    {
        runId = Protocol.RunId(runId);
        var saved = store.ReadTask(runId);
        var maximumLogBytes = saved["promptTemplate"]?["schemaVersion"]?.ToString() == "3" ? ResultLimits.MaximumV3LogBytes : MaximumLogBytes;
        var fileName = stream switch
        {
            "stdout" => "stdout.jsonl",
            "stderr" => "stderr.log",
            _ => throw new ProtocolException("INVALID_LOG_STREAM", "Choose the stdout or stderr log stream.")
        };
        if (cursor < 0 || limitBytes is < 1024 or > MaximumReadBytes)
            throw new ProtocolException("INVALID_LOG_CURSOR", "The log cursor or page size is invalid.", "Use a previous nextCursor and a page size from 1024 to 131072 bytes.");
        var directory = store.RunDirectory(runId);
        var path = Path.Combine(directory, fileName);
        var capped = File.Exists(path + ".truncated");
        try
        {
            if (!File.Exists(path))
            {
                if (cursor != 0) throw InvalidCursor();
                return Result(stream, "", 0, capped, true);
            }
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new ProtocolException("LOG_UNREADABLE", "Task log paths must not be symbolic links or reparse points.");
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var observedLength = file.Length;
            if (cursor > observedLength || cursor > maximumLogBytes) throw InvalidCursor();
            if (cursor > 0)
            {
                file.Position = cursor - 1;
                if (file.ReadByte() != '\n') throw InvalidCursor();
            }
            file.Position = cursor;
            var readableEnd = Math.Min(observedLength, maximumLogBytes);
            capped |= observedLength >= maximumLogBytes;
            var builder = new StringBuilder();
            var raw = new MemoryStream();
            using (raw)
            {
                var buffer = new byte[8192];
                var position = cursor;
                var next = cursor;
                var outputBytes = 0;
                var encodedBytes = 0;
                while (position < readableEnd)
                {
                    var count = file.Read(buffer, 0, (int)Math.Min(buffer.Length, readableEnd - position));
                    if (count == 0) break;
                    for (var index = 0; index < count; index++)
                    {
                        raw.WriteByte(buffer[index]);
                        position++;
                        if (buffer[index] != '\n') continue;
                        var line = OutputRedactor.Redact(StrictUtf8.GetString(raw.GetBuffer(), 0, checked((int)raw.Length)));
                        raw.SetLength(0);
                        var bytes = Encoding.UTF8.GetByteCount(line);
                        var encoded = JsonEncodedText.Encode(line).EncodedUtf8Bytes.Length;
                        if (outputBytes + bytes > limitBytes || encodedBytes + encoded > MaximumEncodedTextBytes)
                        {
                            if (builder.Length > 0) return Result(stream, builder.ToString(), next, capped, false);
                            // A complete oversized line cannot fit on any ordinary page. Show a bounded
                            // UTF-8 prefix, flag the omission, and consume only its complete on-disk line.
                            const string marker = "\n[Log line truncated in this response.]\n";
                            var prefix = Prefix(line, limitBytes - Encoding.UTF8.GetByteCount(marker),
                                MaximumEncodedTextBytes - JsonEncodedText.Encode(marker).EncodedUtf8Bytes.Length);
                            return Result(stream, prefix + marker, position, true, position >= readableEnd);
                        }
                        builder.Append(line);
                        outputBytes += bytes;
                        encodedBytes += encoded;
                        next = position;
                    }
                }
                // Bytes after the last LF may contain half a UTF-8 character. Leave them for a
                // later poll; only the writer can complete that line when its CLI pipe closes.
                return Result(stream, builder.ToString(), next, capped, next >= readableEnd);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new ProtocolException("LOG_UNREADABLE", "The saved CLI log could not be read.", "Keep the task files, check local permissions, and refresh the log.");
        }
    }

    private static string Prefix(string text, int maximumBytes, int maximumEncodedBytes)
    {
        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            var length = middle > 0 && char.IsHighSurrogate(text[middle - 1]) ? middle - 1 : middle;
            var candidate = text[..length];
            if (Encoding.UTF8.GetByteCount(candidate) <= maximumBytes && JsonEncodedText.Encode(candidate).EncodedUtf8Bytes.Length <= maximumEncodedBytes)
                low = middle;
            else high = middle - 1;
        }
        if (low > 0 && char.IsHighSurrogate(text[low - 1])) low--;
        return text[..low];
    }

    private static ProtocolException InvalidCursor() => new("INVALID_LOG_CURSOR", "The log cursor does not identify a complete saved line.", "Refresh this log from cursor 0.");
    private static JsonObject Result(string stream, string text, long cursor, bool truncated, bool eof) => new()
    {
        ["stream"] = stream, ["text"] = text, ["nextCursor"] = cursor, ["truncated"] = truncated, ["eof"] = eof
    };
}

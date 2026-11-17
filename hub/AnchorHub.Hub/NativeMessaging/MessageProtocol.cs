using System.Text.Json;

namespace AnchorHub.Hub.NativeMessaging;

/// <summary>
/// Length-prefixed JSON framing (4-byte LE length + UTF-8 JSON), as
/// used by Firefox native messaging on stdio. The same framing is
/// reused over TCP between forward-mode and listen-mode daemons —
/// this class is bound to a Stream pair, not to stdio specifically.
/// </summary>
internal sealed class MessageProtocol
{
    private readonly Stream _input;
    private readonly Stream _output;

    public MessageProtocol(Stream input, Stream output)
    {
        _input  = input;
        _output = output;
    }

    public static MessageProtocol Stdio() =>
        new(Console.OpenStandardInput(), Console.OpenStandardOutput());

    public async Task<JsonElement?> ReadMessageAsync(CancellationToken ct)
    {
        var lenBuf = new byte[4];
        if (!await ReadExactAsync(_input, lenBuf, ct)) return null; // EOF → peer disconnected

        var len = BitConverter.ToInt32(lenBuf, 0);
        if (len <= 0 || len > 1_048_576) return null;

        var body = new byte[len];
        if (!await ReadExactAsync(_input, body, ct)) return null;

        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    public async Task WriteMessageAsync(object payload, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        var len  = BitConverter.GetBytes(json.Length);
        await _output.WriteAsync(len,  ct);
        await _output.WriteAsync(json, ct);
        await _output.FlushAsync(ct);
    }

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(offset), ct);
            if (n == 0) return false;
            offset += n;
        }
        return true;
    }
}

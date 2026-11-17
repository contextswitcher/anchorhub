using System.IO.Pipelines;
using AnchorHub.Hub.NativeMessaging;

namespace AnchorHub.Hub.Tests;

/// <summary>
/// In-memory bidirectional MessageProtocol pair, simulating the
/// stdio link between a Firefox extension and the forwarder. Each
/// half reads what the other writes.
/// </summary>
internal static class StreamPair
{
    public static (MessageProtocol left, MessageProtocol right) CreateLoopback()
    {
        var leftToRight = new Pipe();
        var rightToLeft = new Pipe();

        var left = new MessageProtocol(
            input:  rightToLeft.Reader.AsStream(),
            output: leftToRight.Writer.AsStream());

        var right = new MessageProtocol(
            input:  leftToRight.Reader.AsStream(),
            output: rightToLeft.Writer.AsStream());

        return (left, right);
    }
}

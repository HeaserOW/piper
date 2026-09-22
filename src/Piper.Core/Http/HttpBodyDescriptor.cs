namespace Piper.Core.Http;

/// <summary>How a message body is delimited on the wire (RFC 9112 6.3).</summary>
public enum HttpBodyFraming
{
    /// <summary>The message carries no body at all.</summary>
    None,

    /// <summary>Exactly <see cref="HttpBodyDescriptor.Length"/> bytes follow the header block.</summary>
    Length,

    /// <summary>Chunked transfer coding; the body ends at the zero-size chunk.</summary>
    Chunked,

    /// <summary>The body runs until the peer closes the connection.</summary>
    UntilClose,
}

/// <summary>
/// How to read one message's body, decided from the header block alone.
/// </summary>
/// <remarks>
/// Separating this from the read is what lets a caller see a response's framing before committing
/// to buffer its body. Every comparable proxy makes the same split at the same point -- it is
/// Fiddler's <c>ResponseHeadersAvailable</c> ("the last event guaranteed to fire before the client
/// starts getting response bytes") and mitmproxy's <c>responseheaders</c> hook.
/// </remarks>
/// <param name="Framing">How the body is delimited.</param>
/// <param name="Length">Byte count for <see cref="HttpBodyFraming.Length"/>; -1 when not known in advance.</param>
public readonly record struct HttpBodyDescriptor(HttpBodyFraming Framing, long Length)
{
    public static HttpBodyDescriptor None => new(HttpBodyFraming.None, 0);

    public static HttpBodyDescriptor OfLength(long length) => new(HttpBodyFraming.Length, length);

    public static HttpBodyDescriptor Chunked => new(HttpBodyFraming.Chunked, -1);

    public static HttpBodyDescriptor UntilClose => new(HttpBodyFraming.UntilClose, -1);

    /// <summary>True when no bytes at all follow the header block.</summary>
    public bool IsEmpty => Framing == HttpBodyFraming.None;
}

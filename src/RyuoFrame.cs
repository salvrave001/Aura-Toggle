using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AuraToggle;

/// <summary>
/// One reply from the Ryuo IV screen. <see cref="Ack"/> is the device's echo of the request's
/// sequence plus one, which is how a reply is told apart from one that belongs to someone else
/// who also has the screen open.
/// </summary>
internal readonly record struct RyuoMessage(string Status, int Ack, string Payload);

/// <summary>
/// The Ryuo IV screen speaks a short text request inside one 1024-byte HID report. The request
/// looks like a one-line HTTP message, wrapped with 0x5A markers, a length and a byte sum.
/// Anything that does not fit in a single report is dropped by the screen, so this refuses to
/// build one.
/// </summary>
internal static class RyuoFrame
{
    /// <summary>USB product id. The vendor id changes between firmware versions; this does not.</summary>
    public const ushort ProductId = 0x1C76;

    /// <summary>How much of a report the screen actually reads. The report id sits in front of it.</summary>
    public const int ReportBytes = 1024;

    private const byte Marker = 0x5A;
    private const byte Escape = 0x5B;

    /// <summary>
    /// Builds one complete frame, markers included, or returns false when it would spill into a
    /// second report. <paramref name="payload"/> is JSON text; an empty string sends no body.
    /// </summary>
    public static bool TryEncode(string method, string resource, int sequence, string payload, out byte[] frame)
    {
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        string header = string.Create(CultureInfo.InvariantCulture,
            $"{method} {resource} 1\r\nContentType=json\r\nContentLength={payloadBytes.Length}\r\nSeqNumber={sequence}\r\n\r\n");
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);

        var body = new byte[headerBytes.Length + payloadBytes.Length];
        Buffer.BlockCopy(headerBytes, 0, body, 0, headerBytes.Length);
        Buffer.BlockCopy(payloadBytes, 0, body, headerBytes.Length, payloadBytes.Length);

        int length = body.Length + 5;
        var raw = new byte[2 + body.Length + 1];
        raw[0] = (byte)(length >> 8);
        raw[1] = (byte)length;
        Buffer.BlockCopy(body, 0, raw, 2, body.Length);

        int sum = 0;
        for (int i = 0; i < raw.Length - 1; i++)
        {
            sum = (sum + raw[i]) & 0xFF;
        }

        raw[^1] = (byte)sum;

        var stuffed = new List<byte>(raw.Length + 8) { Marker };
        foreach (byte value in raw)
        {
            if (value == Marker)
            {
                stuffed.Add(Escape);
                stuffed.Add(0x01);
            }
            else if (value == Escape)
            {
                stuffed.Add(Escape);
                stuffed.Add(0x02);
            }
            else
            {
                stuffed.Add(value);
            }
        }

        stuffed.Add(Marker);
        frame = stuffed.ToArray();
        return frame.Length <= ReportBytes;
    }

    /// <summary>Reverses <see cref="TryEncode"/> for one complete frame, markers included.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> frame, out RyuoMessage message)
    {
        message = default;
        if (frame.Length < 2 || frame[0] != Marker || frame[^1] != Marker)
        {
            return false;
        }

        var raw = new List<byte>(frame.Length);
        for (int i = 1; i < frame.Length - 1; i++)
        {
            if (frame[i] != Escape)
            {
                raw.Add(frame[i]);
                continue;
            }

            if (i + 1 >= frame.Length - 1)
            {
                return false;
            }

            byte next = frame[++i];
            if (next == 0x01)
            {
                raw.Add(Marker);
            }
            else if (next == 0x02)
            {
                raw.Add(Escape);
            }
            else
            {
                return false;
            }
        }

        if (raw.Count < 3)
        {
            return false;
        }

        int sum = 0;
        for (int i = 0; i < raw.Count - 1; i++)
        {
            sum = (sum + raw[i]) & 0xFF;
        }

        if (sum != raw[^1])
        {
            return false;
        }

        int declared = (raw[0] << 8) | raw[1];
        if (declared != raw.Count + 2)
        {
            return false;
        }

        string text = Encoding.UTF8.GetString(raw.GetRange(2, raw.Count - 3).ToArray());
        int split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        string head = split < 0 ? text : text[..split];
        string payload = split < 0 ? "" : text[(split + 4)..];
        int lineBreak = head.IndexOf("\r\n", StringComparison.Ordinal);
        string status = lineBreak < 0 ? head : head[..lineBreak];

        int ack = 0;
        foreach (string line in head.Split("\r\n"))
        {
            if (line.StartsWith("AckNumber=", StringComparison.Ordinal) &&
                int.TryParse(line.AsSpan("AckNumber=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                ack = parsed;
            }
        }

        message = new RyuoMessage(status, ack, payload);
        return true;
    }

    /// <summary>
    /// Pulls the next complete frame out of bytes already read. Padding zeros and a frame that
    /// fails its sum are discarded. Returns false when the start marker has no end yet.
    /// </summary>
    public static bool TryTake(List<byte> pending, out RyuoMessage message)
    {
        message = default;

        while (pending.Count > 0)
        {
            while (pending.Count > 0 && pending[0] != Marker)
            {
                pending.RemoveAt(0);
            }

            int end = -1;
            for (int i = 1; i < pending.Count; i++)
            {
                if (pending[i] == Marker)
                {
                    end = i;
                    break;
                }
            }

            if (end < 0)
            {
                if (pending.Count > 4096)
                {
                    pending.Clear();
                }

                return false;
            }

            var frame = new byte[end + 1];
            pending.CopyTo(0, frame, 0, frame.Length);
            pending.RemoveRange(0, frame.Length);

            if (TryDecode(frame, out message))
            {
                return true;
            }
        }

        return false;
    }
}

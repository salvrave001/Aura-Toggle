using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AuraToggle;

/// <summary>
/// One open Ryuo IV screen. Writes are one report at a time and each one is answered, so a caller
/// holds the link until the matching reply has been read. The screen drops a session that goes
/// quiet for about ten seconds, which is why the host keeps a loop of its own rather than sending
/// only when a control moves.
/// </summary>
internal sealed class RyuoLink : IDisposable
{
    private const int ReplyTimeoutMs = 1200;

    private readonly HidStream _stream;
    private readonly byte[] _readBuffer;
    private readonly List<byte> _pending = new();
    private int _sequence = 1;
    private bool _dead;

    /// <summary>False after a write or a reply failed. The stream cannot be used again.</summary>
    public bool Healthy => !_dead;

    private RyuoLink(HidStream stream)
    {
        _stream = stream;
        _readBuffer = new byte[Math.Max(stream.Info.InputReportLength, 2)];
    }

    public static RyuoLink? TryOpen()
    {
#if REVIEW
        if (AuraFiles.ReviewStore)
        {
            return null;
        }
#endif

        try
        {
            List<HidInfo> present = Hid.Enumerate((_, pid) => pid == RyuoFrame.ProductId, "pid_1c76");
            HidInfo? chosen = present
                .Where(info => info.OutputReportLength >= 512 &&
                    info.Path.Contains("mi_00", StringComparison.OrdinalIgnoreCase))
                .MaxBy(info => info.OutputReportLength);

            if (chosen == null)
            {
                return null;
            }

            HidStream stream = Hid.Open(chosen);
            AuraLog.Info($"Ryuo: {chosen.Product} PID {chosen.Pid:X4}, report {chosen.OutputReportLength}.");
            return new RyuoLink(stream);
        }
        catch (Exception ex) when (ex is IOException or HidAccessException)
        {
            AuraLog.Warn($"Ryuo: screen not opened - {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public bool Send(string method, string resource, string payload)
    {
        if (_dead)
        {
            return false;
        }

        if (!RyuoFrame.TryEncode(method, resource, _sequence, payload, out byte[] frame))
        {
            AuraLog.Warn($"Ryuo: {resource} does not fit in one report.");
            return false;
        }

        int sequence = _sequence++;
        int length = Math.Max(_stream.Info.OutputReportLength, frame.Length + 1);
        var report = new byte[length];
        report[0] = 0x00;
        Buffer.BlockCopy(frame, 0, report, 1, frame.Length);

        try
        {
            _stream.Write(report);
        }
        catch (IOException ex)
        {
            _dead = true;
            AuraLog.Warn($"Ryuo: write failed - {ex.Message}");
            return false;
        }

        return ReadReply(sequence);
    }

    public string[] ListMedia()
    {
        if (!Send("POST", "conn", ""))
        {
            return Array.Empty<string>();
        }

        // Send already consumed the matching reply into the last payload.
        return _lastMedia;
    }

    private string[] _lastMedia = Array.Empty<string>();

    public bool Config(string json) => Send("POST", "config", json);

    public bool Preset(string json) => Send("POST", "preset", json);

    public bool Brightness(string json) => Send("POST", "brightness", json);

    public bool Telemetry(string json) => Send("POST", "all", json);

    public void Disconnect()
    {
        Send("POST", "disconn", "");
    }

    private bool ReadReply(int sequence)
    {
        long deadline = Environment.TickCount64 + ReplyTimeoutMs;
        _lastMedia = Array.Empty<string>();

        while (Environment.TickCount64 < deadline)
        {
            int wait = (int)Math.Max(1, deadline - Environment.TickCount64);
            if (!_stream.Read(_readBuffer, wait))
            {
                _dead = true;
                AuraLog.Warn("Ryuo: no reply.");
                return false;
            }

            // The first byte is the report id. The screen's own bytes follow it.
            for (int i = 1; i < _readBuffer.Length; i++)
            {
                _pending.Add(_readBuffer[i]);
            }

            while (RyuoFrame.TryTake(_pending, out RyuoMessage message))
            {
                if (message.Ack != sequence + 1)
                {
                    continue;
                }

                RememberMedia(message.Payload);
                return true;
            }
        }

        _dead = true;
        AuraLog.Warn("Ryuo: reply was not the one just sent.");
        return false;
    }

    private void RememberMedia(string payload)
    {
        if (payload.Length == 0 || payload[0] != '{')
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("media", out JsonElement media) ||
                media.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var names = new List<string>();
            AddNames(media, "preset", names);
            AddNames(media, "custom", names);
            _lastMedia = names.ToArray();

            if (document.RootElement.TryGetProperty("version", out JsonElement version) &&
                version.TryGetProperty("firmware", out JsonElement firmware) &&
                firmware.ValueKind == JsonValueKind.String)
            {
                AuraLog.Info($"Ryuo: firmware {firmware.GetString()}.");
            }
        }
        catch (JsonException)
        {
        }
    }

    private static void AddNames(JsonElement media, string property, List<string> names)
    {
        if (!media.TryGetProperty(property, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string name = entry.GetString() ?? "";
            if (name.Length > 0 && !names.Contains(name))
            {
                names.Add(name);
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}

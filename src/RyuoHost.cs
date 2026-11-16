using System;
using System.IO;
using System.Threading;

namespace AuraToggle;

/// <summary>How hard the next change has to hit the screen.</summary>
internal enum RyuoApply
{
    /// <summary>Remember it. The screen is not touched.</summary>
    Save,

    /// <summary>Brightness only, so the clip keeps playing.</summary>
    Brightness,

    /// <summary>Colours and slots, without restarting the clip.</summary>
    Live,

    /// <summary>The whole session: clip, layout and, when driving stops, a clean disconnect.</summary>
    Full,
}

internal enum RyuoPresence
{
    Off,
    Driving,
    Missing,
}

/// <summary>
/// Keeps one Ryuo IV screen on the layout the window last set. The screen forgets that layout
/// about ten seconds after the last message, so while driving is on a background thread sends a
/// fresh reading every second for as long as this program is open. That thread is the only one
/// that talks to the screen: the window records the latest layout and the loop sends it, so a
/// burst of slot changes cannot push the keepalive past the ten seconds.
/// </summary>
internal static class RyuoHost
{
    private static readonly object Gate = new();
    private static readonly RyuoSensors Sensors = new();

    private static RyuoLayout _layout = RyuoLayout.Load();
    private static RyuoLink? _link;
    private static Thread? _loop;
    private static bool _run;
    private static bool _closed;
    private static long _quietUntil;
    private static bool _needsConfig = true;
    private static bool _pendingFull;
    private static bool _pendingLive;
    private static bool _pendingBrightness;
    private static bool _wantMedia;
    private static string[] _media = Array.Empty<string>();
    private static RyuoPresence _presence = RyuoPresence.Off;

    public static RyuoPresence Presence => _presence;

    /// <summary>Clip names the screen last reported. Empty until the first listing comes back.</summary>
    public static string[] CachedMedia
    {
        get
        {
            lock (Gate)
            {
                return _media;
            }
        }
    }

    public static RyuoLayout Current
    {
        get
        {
            lock (Gate)
            {
                return Copy(_layout);
            }
        }
    }

    /// <summary>Starts the loop when the saved layout says the screen should be driven. Never blocks.</summary>
    public static void StartIfWanted()
    {
#if REVIEW
        if (AuraFiles.ReviewStore)
        {
            return;
        }
#endif

        RyuoLayout layout = Current;
        if (layout.Drive)
        {
            ThreadPool.QueueUserWorkItem(_ => Apply(layout, RyuoApply.Full));
        }
    }

    public static void Apply(RyuoLayout layout, RyuoApply kind)
    {
        layout.Save();

#if REVIEW
        if (AuraFiles.ReviewStore)
        {
            lock (Gate)
            {
                _layout = Copy(layout);
            }

            return;
        }
#endif

        lock (Gate)
        {
            if (_closed)
            {
                return;
            }

            _layout = Copy(layout);

            if (!layout.Drive)
            {
                StopLocked();
                _presence = RyuoPresence.Off;
                return;
            }

            if (kind == RyuoApply.Save)
            {
                return;
            }

            // Full covers brightness and slots. A link with no session yet has to get the full
            // configuration too, otherwise a colour change would land on the stock clip.
            if (kind == RyuoApply.Full || _needsConfig)
            {
                _pendingFull = true;
            }
            else if (kind == RyuoApply.Brightness)
            {
                _pendingBrightness = true;
            }
            else
            {
                _pendingLive = true;
            }

            EnsureLoop();
        }
    }

    public static string[] Media()
    {
#if REVIEW
        if (AuraFiles.ReviewStore)
        {
            return Array.Empty<string>();
        }
#endif

        lock (Gate)
        {
            if (_closed)
            {
                return Array.Empty<string>();
            }

            // The keepalive owns the open screen. Listing from here as well would steal its
            // reply and the panel would fall back to the dimmed clip.
            if (_run)
            {
                _wantMedia = true;
                return _media;
            }

            if (!EnsureOpen())
            {
                return Array.Empty<string>();
            }

            try
            {
                string[] names = _link!.ListMedia();
                if (names.Length > 0)
                {
                    _media = names;
                }

                return _media;
            }
            finally
            {
                DropLink();
            }
        }
    }

    /// <summary>Tells the screen to drop back to its own clip, then stops the loop.</summary>
    public static void Shutdown()
    {
        Thread? loop;
        lock (Gate)
        {
            _closed = true;
            StopLocked();
            loop = _loop;
            _loop = null;
        }

        loop?.Join(2000);
    }

    private static bool PushConfig(RyuoLayout layout)
    {
        RyuoReading reading = Sensors.Read();
        if (!_link!.Config(layout.ConfigJson(reading.CpuName, reading.GpuName)))
        {
            return false;
        }

        // The screen tears the player down for a config and needs a moment before the next command.
        _needsConfig = false;
        _quietUntil = Environment.TickCount64 + 1600;
        AuraLog.Info("Ryuo: screen layout sent.");
        return true;
    }

    private static void StopLocked()
    {
        _run = false;
        _pendingFull = false;
        _pendingLive = false;
        _pendingBrightness = false;
        _wantMedia = false;
        if (_link != null)
        {
            try
            {
                _link.Disconnect();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }

            DropLink();
        }
    }

    private static bool EnsureOpen()
    {
        if (_link != null)
        {
            if (_link.Healthy)
            {
                return true;
            }

            AuraLog.Warn("Ryuo: the screen link had already failed.");
            DropLink();
        }

        _link = RyuoLink.TryOpen();
        return _link != null;
    }

    private static void DropLink()
    {
        _link?.Dispose();
        _link = null;
        _needsConfig = true;
    }

    private static void Fail(string what)
    {
        AuraLog.Warn($"Ryuo: {what} dropped the session.");
        DropLink();
        _pendingFull = true;
        _presence = RyuoPresence.Missing;
    }

    private static void EnsureLoop()
    {
        _run = true;
        if (_loop is { IsAlive: true })
        {
            return;
        }

        _loop = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Ryuo",
        };
        AuraLog.Info("Ryuo: keepalive started.");
        _loop.Start();
    }

    private static void Loop()
    {
        try
        {
            while (true)
            {
                int pause;
                lock (Gate)
                {
                    if (!_run || _closed)
                    {
                        return;
                    }

                    try
                    {
                        pause = Tick();
                    }
                    catch (Exception ex)
                    {
                        AuraLog.Error("Ryuo", ex);
                        DropLink();
                        _pendingFull = true;
                        _presence = RyuoPresence.Missing;
                        pause = 1000;
                    }
                }

                Thread.Sleep(pause);
            }
        }
        finally
        {
            lock (Gate)
            {
                if (_loop == Thread.CurrentThread)
                {
                    _loop = null;
                    if (_run && !_closed)
                    {
                        EnsureLoop();
                    }
                }
            }
        }
    }

    /// <summary>One pass of the loop. Returns how long to wait before the next one.</summary>
    private static int Tick()
    {
        if (!EnsureOpen())
        {
            _presence = RyuoPresence.Missing;
            return 1000;
        }

        // A configuration restarts the player. Nothing else may be sent until it has settled,
        // including a slot change that arrived while it was settling.
        if (Environment.TickCount64 < _quietUntil)
        {
            return 100;
        }

        if (_needsConfig || _pendingFull)
        {
            if (!PushConfig(_layout))
            {
                Fail("config");
                return 1000;
            }

            _pendingFull = false;
            _pendingLive = false;
            _pendingBrightness = false;
            NoteDriving();
            return 100;
        }

        if (_pendingLive)
        {
            if (!_link!.Preset(_layout.PresetJson()))
            {
                Fail("preset");
                return 1000;
            }

            _pendingLive = false;
            AuraLog.Info("Ryuo: indicators updated.");
        }

        if (_pendingBrightness)
        {
            if (!_link!.Brightness(_layout.BrightnessJson()))
            {
                Fail("brightness");
                return 1000;
            }

            _pendingBrightness = false;
        }

        RyuoReading reading = Sensors.Read();
        if (!_link!.Telemetry(Sensors.TelemetryJson(reading)))
        {
            Fail("telemetry");
            return 1000;
        }

        NoteDriving();

        if (_wantMedia)
        {
            string[] names = _link.ListMedia();
            _wantMedia = false;
            if (!_link.Healthy)
            {
                Fail("conn");
                return 1000;
            }

            if (names.Length > 0)
            {
                _media = names;
            }
        }

        return 1000;
    }

    private static void NoteDriving()
    {
        if (_presence != RyuoPresence.Driving)
        {
            AuraLog.Info("Ryuo: screen is being driven.");
        }

        _presence = RyuoPresence.Driving;
    }

    private static RyuoLayout Copy(RyuoLayout layout) => new()
    {
        Drive = layout.Drive,
        Brightness = layout.Brightness,
        TitleColor = layout.TitleColor,
        ContentColor = layout.ContentColor,
        PlayMode = layout.PlayMode,
        Media = layout.Media,
        Items = (string[])layout.Items.Clone(),
    };
}

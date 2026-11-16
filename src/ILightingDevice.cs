using System;
using System.Collections.Generic;

namespace AuraToggle;

/// <summary>
/// One lighting controller the switch can talk to. The mainboard is a USB HID device; G.Skill
/// DRAM is an SMBus device. Both take the same per-channel look, so on, off and presets do not
/// grow a second path.
/// </summary>
internal interface ILightingDevice : IDisposable
{
    /// <summary>Stable id for channel records. Survives a reboot for the same physical device.</summary>
    string Key { get; }

    /// <summary>What the selector shows for this controller.</summary>
    string Name { get; }

    /// <summary>Channels in protocol order.</summary>
    List<AuraChannel> Channels { get; }

    /// <summary>Applies one burst. Volatile only: nothing here may commit controller flash.</summary>
    void Apply(IReadOnlyList<ChannelLook> looks);
}

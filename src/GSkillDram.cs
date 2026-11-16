using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AuraToggle;

/// <summary>
/// G.Skill Trident Z / Trident Z5 Neo RGB. The sticks use an ENE controller on the DRAM SMBus,
/// the same family ASUS Aura DRAM uses, and the effect numbers match the mainboard's.
/// Writes stay in the RGB registers. The apply value that commits flash (0xAA) is refused,
/// and SPD / PMIC addresses are not in the address list at all.
/// </summary>
internal sealed class GSkillDram : ILightingDevice
{
    internal const string DeviceKey = "gskill-tridentz";

    private const byte Broadcast = 0x77;
    private const ushort RegDirect = 0x8020;
    private const ushort RegMode = 0x8021;
    private const ushort RegSpeed = 0x8022;
    private const ushort RegDirection = 0x8023;
    private const ushort RegApply = 0x80A0;
    private const ushort RegSlot = 0x80F8;
    private const ushort RegAddress = 0x80F9;
    private const ushort RegName = 0x1000;
    private const ushort RegMicron = 0x1030;
    private const ushort RegConfig = 0x1C00;
    private const byte SpeedNormal = 0x02;

    /// <summary>Addresses a stick may be moved to. 0x77 is the broadcast, never a destination.</summary>
    private static readonly byte[] Addresses =
    {
        0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76,
        0x78, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F,
        0x4F, 0x66, 0x67, 0x39, 0x3A, 0x3B, 0x3C, 0x3D,
    };

    private readonly AmdSmBus _bus;
    private readonly List<Stick> _sticks;

    private GSkillDram(AmdSmBus bus, List<Stick> sticks)
    {
        _bus = bus;
        _sticks = sticks;
        Channels = new List<AuraChannel>(sticks.Count);
        foreach (Stick stick in sticks)
        {
            Channels.Add(new AuraChannel(stick.Slot, Onboard: false, Header: stick.Slot + 1, Label: stick.Label));
        }
    }

    public string Key => DeviceKey;

    public string Name => "G.Skill Trident Z";

    public List<AuraChannel> Channels { get; }

    /// <summary>The kit when it answers, otherwise null. Never throws for "not installed".</summary>
    internal static GSkillDram? TryDiscover()
    {
#if REVIEW
        if (AuraFiles.ReviewStore)
        {
            return null;
        }
#endif

        try
        {
            AmdSmBus? bus = AmdSmBus.Open();
            if (bus == null)
            {
                return null;
            }

            using (bus.Acquire())
            {
                List<Stick> sticks = Scan(bus);
                if (sticks.Count == 0 && SpeaksEne(bus, Broadcast))
                {
                    Remap(bus);
                    sticks = Scan(bus);
                }

                if (sticks.Count == 0)
                {
                    AuraLog.Info("DRAM: no G.Skill ENE module answered.");
                    return null;
                }

                foreach (Stick stick in sticks)
                {
                    AuraLog.Info($"DRAM: {stick.Label} at 0x{stick.Address:X2}, {stick.Version}, {stick.Leds} LED(s).");
                }

                return new GSkillDram(bus, sticks);
            }
        }
        catch (Exception ex) when (ex is IOException or DllNotFoundException or EntryPointNotFoundException
                                       or InvalidOperationException)
        {
            AuraLog.Warn($"DRAM: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public void Apply(IReadOnlyList<ChannelLook> looks)
    {
        try
        {
            using (_bus.Acquire())
            {
                foreach (ChannelLook look in looks)
                {
                    Stick? stick = _sticks.Find(entry => entry.Slot == look.Channel);
                    if (stick == null)
                    {
                        continue;
                    }

                    ApplyStick(stick, AuraDevice.Verified(look.Mode), look.Red, look.Green, look.Blue);
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            throw new IOException(ex.Message, ex);
        }
    }

    public void Dispose()
    {
    }

    private void ApplyStick(Stick stick, byte mode, byte red, byte green, byte blue)
    {
        WriteReg(stick.Address, RegDirect, 0x00);

        if (mode == AuraState.ModeOff)
        {
            red = 0;
            green = 0;
            blue = 0;
        }

        if (AuraDevice.CarriesColour(mode))
        {
            byte[] colors = EneProtocol.ColorBuffer(stick.Leds, red, green, blue);
            foreach (ushort register in stick.ColorRegisters)
            {
                for (int i = 0; i < colors.Length; i++)
                {
                    WriteReg(stick.Address, (ushort)(register + i), colors[i]);
                }
            }

            WriteReg(stick.Address, RegApply, 0x01);
        }

        WriteReg(stick.Address, RegMode, mode);
        if (mode != AuraState.ModeOff)
        {
            WriteReg(stick.Address, RegSpeed, SpeedNormal);
            WriteReg(stick.Address, RegDirection, 0x00);
        }

        WriteReg(stick.Address, RegApply, 0x01);
        int readBack = ReadReg(_bus, stick.Address, RegMode);
        AuraLog.Info(string.Format(CultureInfo.InvariantCulture,
            "DRAM: {0} mode 0x{1:X2}, controller reads 0x{2:X2}.", stick.Label, mode, readBack));
    }

    private static List<Stick> Scan(AmdSmBus bus)
    {
        var found = new List<Stick>();
        var used = new HashSet<int>();

        foreach (byte address in Addresses)
        {
            if (!IsEne(bus, address))
            {
                continue;
            }

            string version = ReadText(bus, address, RegName, 16);
            int slot = ReadReg(bus, address, RegSlot);
            if (slot < 0 || slot > 7 || !used.Add(slot))
            {
                slot = 0;
                while (!used.Add(slot) && slot < 8)
                {
                    slot++;
                }
            }

            byte[] config = new byte[4];
            for (int i = 0; i < config.Length; i++)
            {
                int value = ReadReg(bus, address, (ushort)(RegConfig + i));
                config[i] = value < 0 ? (byte)0 : (byte)value;
            }

            found.Add(new Stick(
                address,
                slot,
                EneProtocol.SlotLabel(slot),
                version,
                EneProtocol.LedCount(config),
                EneProtocol.EffectRegisters(version)));
        }

        found.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        return found;
    }

    private static void Remap(AmdSmBus bus)
    {
        int next = 0;
        for (int slot = 0; slot < 8; slot++)
        {
            if (bus.ReadByte(Broadcast) < 0)
            {
                break;
            }

            while (next < Addresses.Length && bus.ReadByte(Addresses[next]) >= 0)
            {
                next++;
            }

            if (next >= Addresses.Length)
            {
                break;
            }

            byte target = Addresses[next++];
            AuraLog.Info($"DRAM: moving a module at 0x77 to 0x{target:X2} (slot {slot}).");
            WriteChecked(bus, Broadcast, RegSlot, (byte)slot);
            WriteChecked(bus, Broadcast, RegAddress, (byte)(target << 1));
        }
    }

    private static bool SpeaksEne(AmdSmBus bus, byte address) => ReadReg(bus, address, RegName) >= 0;

    private static bool IsEne(AmdSmBus bus, byte address)
    {
        for (int i = 0; i < 16; i++)
        {
            if (bus.ReadByteData(address, (byte)(0xA0 + i)) != i)
            {
                return false;
            }
        }

        return ReadText(bus, address, RegMicron, 6) != "Micron";
    }

    private static string ReadText(AmdSmBus bus, byte address, ushort register, int length)
    {
        var text = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            int value = ReadReg(bus, address, (ushort)(register + i));
            if (value <= 0 || value >= 127)
            {
                break;
            }

            text.Append((char)value);
        }

        return text.ToString();
    }

    private static int ReadReg(AmdSmBus bus, byte address, ushort register)
    {
        if (!bus.WriteWordData(address, 0x00, EneProtocol.WireWord(register)))
        {
            return -1;
        }

        return bus.ReadByteData(address, 0x81);
    }

    private void WriteReg(byte address, ushort register, byte value)
    {
        EneProtocol.EnsureWrite(register, value);
        WriteChecked(_bus, address, register, value);
    }

    private static void WriteChecked(AmdSmBus bus, byte address, ushort register, byte value)
    {
        EneProtocol.EnsureWrite(register, value);
        if (!Allowed(address) ||
            !bus.WriteWordData(address, 0x00, EneProtocol.WireWord(register)) ||
            !bus.WriteByteData(address, 0x01, value))
        {
            string detail = string.Format(CultureInfo.InvariantCulture,
                "DRAM register 0x{0:X4} was not accepted at 0x{1:X2}.", register, address);
            var error = new IOException(detail);
            throw error;
        }
    }

    private static bool Allowed(byte address) => address == Broadcast || Array.IndexOf(Addresses, address) >= 0;

    private sealed record Stick(
        byte Address, int Slot, string Label, string Version, int Leds, ushort[] ColorRegisters);
}

/// <summary>Pure ENE register encoding, covered without touching the bus.</summary>
internal static class EneProtocol
{
    internal static ushort WireWord(ushort register) => (ushort)((register << 8) | (register >> 8));

    internal static byte[] ColorBuffer(int leds, byte red, byte green, byte blue)
    {
        var buffer = new byte[leds * 3];
        for (int led = 0; led < leds; led++)
        {
            buffer[led * 3] = red;
            buffer[(led * 3) + 1] = blue;
            buffer[(led * 3) + 2] = green;
        }

        return buffer;
    }

    internal static int LedCount(ReadOnlySpan<byte> config)
    {
        int primary = config.Length > 2 ? config[2] : 0;
        int secondary = config.Length > 3 ? config[3] : 0;
        int count = Math.Max(primary, secondary);
        return count is >= 1 and <= 16 ? count : 8;
    }

    /// <summary>
    /// E6K5 firmware keeps effect colours at 0x8160. Everything else this kit reports, including
    /// a version that could not be read, is written at 0x8010. An empty version is written to
    /// both so a stick still changes colour.
    /// </summary>
    internal static ushort[] EffectRegisters(string version)
    {
        if (version.Contains("E6K5", StringComparison.Ordinal))
        {
            return new ushort[] { 0x8160 };
        }

        return version.Length == 0 ? new ushort[] { 0x8010, 0x8160 } : new ushort[] { 0x8010 };
    }

    internal static string SlotLabel(int slot) =>
        string.Format(CultureInfo.InvariantCulture, "DIMM {0}", slot + 1);

    internal static void EnsureWrite(ushort register, byte value)
    {
        if (register is >= 0x8010 and < 0x8010 + 48 || register is >= 0x8160 and < 0x8160 + 48)
        {
            return;
        }

        bool allowed = register switch
        {
            0x8020 => value <= 1,
            0x8021 => value == AuraState.ModeOff || AuraPresets.ByMode(value) != null,
            0x8022 => value <= 4,
            0x8023 => value == 0,
            0x80A0 => value == 0x01,
            0x80F8 => value <= 7,
            0x80F9 => (value & 1) == 0 && Array.IndexOf(DestinationAddresses(), (byte)(value >> 1)) >= 0,
            _ => false,
        };

        if (!allowed)
        {
            throw new InvalidOperationException(
                $"Refusing DRAM register 0x{register:X4} value 0x{value:X2}.");
        }
    }

    private static byte[] DestinationAddresses() => new byte[]
    {
        0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76,
        0x78, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F,
        0x4F, 0x66, 0x67, 0x39, 0x3A, 0x3B, 0x3C, 0x3D,
    };
}

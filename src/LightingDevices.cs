using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AuraToggle;

/// <summary>
/// The mainboard controllers plus G.Skill DRAM, as one list. DRAM is optional: a machine
/// without the modules, or without the port driver, still switches the board.
/// </summary>
internal static class LightingDevices
{
    internal static List<ILightingDevice> DiscoverAll()
    {
        if (!TryDiscoverAll(out List<ILightingDevice> devices, out int errorExitCode))
        {
            throw errorExitCode switch
            {
                4 => new AuraNotFoundException(Strings.ErrorControllerBusy, 4),
                5 => new IOException(Strings.ErrorWriteGeneric),
                _ => new AuraNotFoundException(Strings.ErrorControllerNotFound, 3),
            };
        }

        return devices;
    }

    internal static bool TryDiscoverAll(out List<ILightingDevice> devices, out int errorExitCode)
    {
        devices = new List<ILightingDevice>();
        int auraCode = 0;

        try
        {
            devices.AddRange(AuraDevice.DiscoverAll());
        }
        catch (AuraNotFoundException ex)
        {
            auraCode = ex.ExitCode;
        }
        catch (IOException)
        {
            auraCode = 5;
        }

        GSkillDram? memory = GSkillDram.TryDiscover();
        if (memory != null)
        {
            devices.Add(memory);
        }

        if (devices.Count > 0)
        {
            errorExitCode = 0;
            return true;
        }

        errorExitCode = auraCode == 0 ? 3 : auraCode;
        return false;
    }

    internal static List<AuraDeviceSummary> ListDevices(out int errorExitCode)
    {
        using (AuraFiles.HardwareLock())
        {
            if (!TryDiscoverAll(out List<ILightingDevice> devices, out errorExitCode))
            {
                return new List<AuraDeviceSummary>();
            }

            try
            {
                return Summarize(devices);
            }
            finally
            {
                foreach (ILightingDevice device in devices)
                {
                    device.Dispose();
                }
            }
        }
    }

    internal static List<AuraDeviceSummary> Summarize(IReadOnlyList<ILightingDevice> devices)
    {
        int unnamed = 0;
        var summaries = new List<AuraDeviceSummary>(devices.Count);
        foreach (ILightingDevice device in devices)
        {
            string name = device.Name.Length > 0
                ? device.Name
                : string.Format(CultureInfo.CurrentCulture, Strings.DeviceFallbackName, ++unnamed);
            summaries.Add(new AuraDeviceSummary(device.Key, name, device.Channels));
        }

        return summaries;
    }
}

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AuraToggle;

/// <summary>
/// Byte port I/O through inpoutx64, the signed driver that can read the AMD SMBus registers.
/// The DRAM RGB controller is not a USB device, so the HID path used for the mainboard cannot
/// reach it. Nothing in here maps physical memory.
/// </summary>
internal static class PortIo
{
    internal const string InstallArgument = "--install-port-driver";

    private const string Library = "inpoutx64.dll";

    private static bool _resolverReady;
    private static bool _asked;

    /// <summary>
    /// The window sets this. A scheduled <c>-off</c> must not pop a UAC dialog at night, and
    /// the one-time driver install waits for a person to approve it.
    /// </summary>
    internal static bool PromptAllowed { get; set; }

    internal static bool IsReady()
    {
        try
        {
            EnsureResolver();
            return IsInpOutDriverOpen();
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens the driver, asking once for elevation when the file is present but the driver is
    /// not installed yet. Returns false when RAM cannot be reached; the caller leaves it alone.
    /// </summary>
    internal static bool TryEnsure()
    {
        if (IsReady())
        {
            return true;
        }

        string path = DllPath();
        if (!File.Exists(path))
        {
            AuraLog.Warn("DRAM: inpoutx64.dll is not next to the program, RAM lighting stays untouched.");
            return false;
        }

        if (!PromptAllowed || _asked)
        {
            AuraLog.Warn("DRAM: the port driver is not installed, RAM lighting stays untouched.");
            return false;
        }

        _asked = true;
        AuraLog.Info("DRAM: port driver is not installed, asking for permission.");

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? "",
                Arguments = InstallArgument,
                UseShellExecute = true,
                Verb = "runas",
            });

            if (process == null || !process.WaitForExit(20000))
            {
                AuraLog.Warn("DRAM: port driver install did not finish.");
                return false;
            }
        }
        catch (Win32Exception)
        {
            AuraLog.Warn("DRAM: port driver install was declined.");
            return false;
        }

        return IsReady();
    }

    /// <summary>Run elevated: loads the driver and returns 0 when it opened.</summary>
    internal static int InstallElevated()
    {
        try
        {
            EnsureResolver();
            // Reading the POST diagnostic port is what makes the DLL install its driver.
            // The value itself is unused.
            _ = DlPortReadPortUchar(0x80);
            return IsInpOutDriverOpen() ? 0 : 1;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException)
        {
            AuraLog.Error("DRAM driver", ex);
            return 1;
        }
    }

    internal static byte Read(ushort port)
    {
        EnsureResolver();
        return DlPortReadPortUchar(port);
    }

    internal static void Write(ushort port, byte value)
    {
        EnsureResolver();
        DlPortWritePortUchar(port, value);
    }

    private static string DllPath() => Path.Combine(AppContext.BaseDirectory, Library);

    private static void EnsureResolver()
    {
        if (_resolverReady)
        {
            return;
        }

        // The assembly only searches System32, so the DLL beside the exe would be invisible
        // to a plain DllImport. This resolver is the one exception, and only for that file.
        NativeLibrary.SetDllImportResolver(typeof(PortIo).Assembly, Resolve);
        _resolverReady = true;
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals(Library, StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        string path = DllPath();
        return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
    }

    [DllImport(Library)]
    private static extern bool IsInpOutDriverOpen();

    [DllImport(Library)]
    private static extern byte DlPortReadPortUchar(ushort port);

    [DllImport(Library)]
    private static extern void DlPortWritePortUchar(ushort port, byte value);
}

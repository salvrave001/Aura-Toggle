using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;

namespace AuraToggle;

/// <summary>One sample of the numbers the screen can show. Temperatures stay at 0 when Windows has none.</summary>
internal readonly record struct RyuoReading(
    int CpuLoad,
    int CpuTemperature,
    int CpuMhz,
    int MemoryTotalMb,
    int MemoryUsedMb,
    int MemoryLoad,
    int GpuLoad,
    int GpuTemperature,
    int GpuMhz,
    int GpuPower,
    bool HasGpu,
    int DiskTotalGb,
    int DiskUsedGb,
    string CpuName,
    string GpuName);

/// <summary>
/// Readings the screen asks for, taken without a kernel driver. Load, clock, memory and the clock
/// time are always available. A graphics card is read through nvidia-smi when that tool is present.
/// Processor temperature is whatever the firmware's thermal zone reports, which on a desktop board
/// is sometimes a real number and sometimes nothing.
/// </summary>
internal sealed class RyuoSensors
{
    private long _idle;
    private long _kernel;
    private long _user;
    private bool _haveTimes;

    private int _temperature;
    private long _temperatureAt;
    private int _temperatureBusy;

    private RyuoGpu _gpu = new("", 0, 0, 0, 0, false);
    private long _gpuAt = long.MinValue;
    private int _gpuBusy;
    private bool _gpuMissing;

    private readonly string _cpuName = ReadCpuName();

    public RyuoReading Read()
    {
        try
        {
            (int load, int mhz) = Cpu();
            (int total, int used, int memoryLoad) = Memory();
            (int diskTotal, int diskUsed) = Disk();
            RyuoGpu gpu = Gpu();
            return new RyuoReading(
                load,
                Temperature(),
                mhz,
                total,
                used,
                memoryLoad,
                gpu.Load,
                gpu.Temperature,
                gpu.Mhz,
                gpu.Power,
                gpu.Present,
                diskTotal,
                diskUsed,
                _cpuName,
                gpu.Name);
        }
        catch (Exception ex)
        {
            // A failed sample must not take down the keepalive. The screen dims for good once
            // that loop stops, so a zero reading is the safe result.
            AuraLog.Error("Ryuo sensors", ex);
            return new RyuoReading(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, 0, 0, _cpuName, "");
        }
    }

    public string TelemetryJson(RyuoReading reading)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("network");
            writer.WriteStartObject();
            writer.WriteNumber("upload", 0);
            writer.WriteNumber("download", 0);
            writer.WriteEndObject();

            writer.WritePropertyName("memory");
            writer.WriteStartObject();
            writer.WriteNumber("total", reading.MemoryTotalMb);
            writer.WriteNumber("used", reading.MemoryUsedMb);
            writer.WriteNumber("load", reading.MemoryLoad);
            writer.WriteNumber("temperature", 0);
            writer.WriteNumber("speed", 0);
            writer.WriteEndObject();

            writer.WritePropertyName("cpu");
            writer.WriteStartObject();
            writer.WriteNumber("load", reading.CpuLoad);
            writer.WriteNumber("temperature", reading.CpuTemperature);
            writer.WriteNumber("temperaturePackage", reading.CpuTemperature);
            writer.WriteNumber("speedAverage", reading.CpuMhz);
            writer.WriteNumber("power", 0);
            writer.WriteNumber("voltage", 0);
            writer.WriteNumber("usage", reading.CpuLoad);
            writer.WriteEndObject();

            writer.WritePropertyName("gpu");
            writer.WriteStartObject();
            writer.WriteBoolean("hasDedicated", reading.HasGpu);
            writer.WriteNumber("load", reading.GpuLoad);
            writer.WriteNumber("temperature", reading.GpuTemperature);
            writer.WriteNumber("fan", 0);
            writer.WriteNumber("speed", reading.GpuMhz);
            writer.WriteNumber("power", reading.GpuPower);
            writer.WriteNumber("voltage", 0);
            writer.WriteEndObject();

            writer.WritePropertyName("disk");
            writer.WriteStartObject();
            writer.WriteNumber("total", reading.DiskTotalGb);
            writer.WriteNumber("used", reading.DiskUsedGb);
            writer.WriteNumber("load", reading.DiskTotalGb == 0
                ? 0
                : reading.DiskUsedGb * 100 / reading.DiskTotalGb);
            writer.WriteNumber("activity", 0);
            writer.WriteNumber("temperature", 0);
            writer.WriteNumber("readSpeed", 0);
            writer.WriteNumber("writeSpeed", 0);
            writer.WriteEndObject();

            writer.WritePropertyName("fans");
            writer.WriteStartArray();
            writer.WriteEndArray();

            writer.WritePropertyName("motherboard");
            writer.WriteStartObject();
            writer.WriteNumber("temperature", 0);
            writer.WriteNumber("chipsetTemperature", 0);
            writer.WriteEndObject();

            writer.WriteNumber("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private (int Load, int Mhz) Cpu()
    {
        int load = 0;
        if (GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user))
        {
            long idleNow = Ticks(idle);
            long kernelNow = Ticks(kernel);
            long userNow = Ticks(user);
            if (_haveTimes)
            {
                long idleDelta = idleNow - _idle;
                long total = (kernelNow - _kernel) + (userNow - _user);
                if (total > 0)
                {
                    load = (int)Math.Clamp((total - idleDelta) * 100 / total, 0, 100);
                }
            }

            _idle = idleNow;
            _kernel = kernelNow;
            _user = userNow;
            _haveTimes = true;
        }

        return (load, Clock());
    }

    private static int Clock()
    {
        int count = Environment.ProcessorCount;
        int stride = Marshal.SizeOf<ProcessorPowerInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(stride * count);
        try
        {
            if (CallNtPowerInformation(11, IntPtr.Zero, 0, buffer, stride * count) != 0)
            {
                return 0;
            }

            long sum = 0;
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<ProcessorPowerInformation>(buffer + (i * stride));
                sum += info.CurrentMhz;
            }

            return (int)(sum / count);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (int Total, int Used, int Load) Memory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
        {
            return (0, 0, 0);
        }

        int total = (int)(status.ullTotalPhys / (1024 * 1024));
        int free = (int)(status.ullAvailPhys / (1024 * 1024));
        return (total, Math.Max(0, total - free), (int)Math.Clamp(status.dwMemoryLoad, 0, 100));
    }

    private static (int Total, int Used) Disk()
    {
        try
        {
            string root = Path.GetPathRoot(Environment.SystemDirectory) ?? "";
            if (root.Length == 0)
            {
                return (0, 0);
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return (0, 0);
            }

            int total = (int)(drive.TotalSize / (1024L * 1024 * 1024));
            int free = (int)(drive.AvailableFreeSpace / (1024L * 1024 * 1024));
            return (total, Math.Max(0, total - free));
        }
        catch (IOException)
        {
            return (0, 0);
        }
    }

    private int Temperature()
    {
        long now = Environment.TickCount64;
        if (now - _temperatureAt < 3000)
        {
            return _temperature;
        }

        // The thermal-zone query can sit forever. It runs off the keepalive thread and the last
        // value (or zero) is what gets sent until it returns.
        if (Interlocked.CompareExchange(ref _temperatureBusy, 1, 0) != 0)
        {
            return _temperature;
        }

        _temperatureAt = now;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                _temperature = ReadTemperature();
            }
            catch (Exception ex)
            {
                AuraLog.Error("Ryuo temperature", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _temperatureBusy, 0);
            }
        });
        return _temperature;
    }

    private static int ReadTemperature()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            foreach (ManagementObject zone in searcher.Get())
            {
                using (zone)
                {
                    if (zone["CurrentTemperature"] is not object raw)
                    {
                        continue;
                    }

                    int celsius = (Convert.ToInt32(raw, CultureInfo.InvariantCulture) / 10) - 273;
                    if (celsius is >= 15 and <= 105)
                    {
                        return celsius;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
        }

        return 0;
    }

    private RyuoGpu Gpu()
    {
        long now = Environment.TickCount64;
        int wait = _gpuMissing || !_gpu.Present ? 30000 : 2000;
        // _gpuAt starts at long.MinValue, and subtracting that overflows, so the first sample
        // must not be treated as a fresh cache hit.
        if (_gpuAt != long.MinValue && now - _gpuAt < wait)
        {
            return _gpu;
        }

        if (Interlocked.CompareExchange(ref _gpuBusy, 1, 0) != 0)
        {
            return _gpu;
        }

        _gpuAt = now;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (!TryNvidia(out RyuoGpu reading))
                {
                    _gpuMissing = true;
                    _gpu = new RyuoGpu("", 0, 0, 0, 0, false);
                    return;
                }

                _gpuMissing = false;
                if (reading.Name.Length == 0)
                {
                    reading = reading with { Name = _gpu.Name };
                }

                _gpu = reading;
            }
            catch (Exception ex)
            {
                AuraLog.Error("Ryuo gpu", ex);
                _gpuMissing = true;
            }
            finally
            {
                Interlocked.Exchange(ref _gpuBusy, 0);
            }
        });
        return _gpu;
    }

    private static bool TryNvidia(out RyuoGpu reading)
    {
        reading = default;
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,temperature.gpu,utilization.gpu,clocks.sm,power.draw --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (!process.Start())
            {
                return false;
            }

            string output = "";
            if (!process.WaitForExit(800))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return false;
            }

            output = process.StandardOutput.ReadToEnd();
            if (process.ExitCode != 0 || output.Length == 0)
            {
                return false;
            }

            string line = output.Split('\n', 2)[0].Trim();
            string[] parts = line.Split(',');
            if (parts.Length < 5)
            {
                return false;
            }

            reading = new RyuoGpu(
                parts[0].Trim(),
                Number(parts[1]),
                Number(parts[2]),
                Number(parts[3]),
                Number(parts[4]),
                true);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static int Number(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? (int)Math.Round(value)
            : 0;

    private static string ReadCpuName()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            string name = key?.GetValue("ProcessorNameString") as string ?? "";
            return name.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "";
        }
    }

    private static long Ticks(FILETIME time) => ((long)time.High << 32) | (uint)time.Low;

    private readonly record struct RyuoGpu(string Name, int Temperature, int Load, int Mhz, int Power, bool Present);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public int Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPowerInformation
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(int level, IntPtr input, int inputLength, IntPtr output,
        int outputLength);
}

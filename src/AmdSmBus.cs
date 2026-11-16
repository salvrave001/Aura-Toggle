using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace AuraToggle;

/// <summary>
/// AMD FCH SMBus host (PCI 1022:790B), the bus the DRAM RGB controllers sit on.
/// Talks the PIIX4 register set at the controller's fixed I/O block. SPD hubs (0x50-0x57)
/// and DDR5 PMICs are never addressed.
/// </summary>
internal sealed class AmdSmBus
{
    internal const ushort PrimaryBase = 0x0B00;
    internal const ushort AuxiliaryBase = 0x0B20;

    private const byte HostBusy = 0x01;
    private const byte Interrupt = 0x02;
    private const byte DeviceError = 0x04;
    private const byte BusError = 0x08;
    private const byte Failed = 0x10;
    private const byte StatusClear = Interrupt | DeviceError | BusError | Failed;
    private const byte Start = 0x40;
    private const byte Kill = 0x02;
    private const byte ProtocolByte = 0x04;
    private const byte ProtocolByteData = 0x08;
    private const byte ProtocolWordData = 0x0C;

    private readonly ushort _base;
    private bool _held;
    private byte _semaphore;

    private AmdSmBus(ushort ioBase) => _base = ioBase;

    /// <summary>
    /// The primary DRAM bus when it answers, otherwise the auxiliary block. Null when this
    /// machine has no live AMD SMBus at the addresses this controller uses.
    /// </summary>
    internal static AmdSmBus? Open()
    {
        if (!PortIo.TryEnsure() || !MachineHasAmdSmBus())
        {
            return null;
        }

        if (Present(PrimaryBase))
        {
            AuraLog.Info($"DRAM: AMD SMBus at I/O 0x{PrimaryBase:X4}.");
            return new AmdSmBus(PrimaryBase);
        }

        if (Present(AuxiliaryBase))
        {
            AuraLog.Info($"DRAM: AMD SMBus at I/O 0x{AuxiliaryBase:X4}.");
            return new AmdSmBus(AuxiliaryBase);
        }

        AuraLog.Info("DRAM: AMD SMBus device is present but the I/O block does not answer.");
        return null;
    }

    internal IDisposable Acquire()
    {
        byte count = ReadOffset(0x08);
        if (count == 0xFF)
        {
            throw Bus("SMBus semaphore does not answer.");
        }

        for (int attempt = 0; attempt < 50; attempt++)
        {
            WriteOffset(0x08, (byte)(count | 0x10));
            count = ReadOffset(0x08);
            if ((count & 0x10) != 0)
            {
                _semaphore = count;
                _held = true;
                return new Release(this);
            }

            Thread.Sleep(1);
        }

        if (count != 0xFF)
        {
            WriteOffset(0x08, (byte)(count | 0x20));
        }

        throw Bus("SMBus semaphore is busy.");
    }

    /// <summary>Reads one byte with no command. Negative when the address does not answer.</summary>
    internal int ReadByte(byte address) =>
        Transact(address, read: true, ProtocolByte, command: null, data0: null, data1: null);

    /// <summary>SMBus read-byte-data. Negative when the address does not answer.</summary>
    internal int ReadByteData(byte address, byte command) =>
        Transact(address, read: true, ProtocolByteData, command, data0: null, data1: null);

    internal bool WriteByteData(byte address, byte command, byte value) =>
        Transact(address, read: false, ProtocolByteData, command, value, data1: null) >= 0;

    internal bool WriteWordData(byte address, byte command, ushort value) =>
        Transact(address, read: false, ProtocolWordData, command, (byte)value, (byte)(value >> 8)) >= 0;

    private int Transact(byte address, bool read, byte protocol, byte? command, byte? data0, byte? data1)
    {
        if (!WaitIdle())
        {
            throw Bus("SMBus is busy.");
        }

        byte status = ReadOffset(0x00);
        if ((status & StatusClear) != 0)
        {
            WriteOffset(0x00, (byte)(status & StatusClear));
        }

        if (command.HasValue)
        {
            WriteOffset(0x03, command.Value);
        }

        if (data0.HasValue)
        {
            WriteOffset(0x05, data0.Value);
        }

        if (data1.HasValue)
        {
            WriteOffset(0x06, data1.Value);
        }

        WriteOffset(0x04, (byte)((address << 1) | (read ? 1 : 0)));
        WriteOffset(0x02, (byte)(Start | protocol));

        var wait = Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < 50)
        {
            status = ReadOffset(0x00);
            if ((status & HostBusy) != 0)
            {
                Thread.SpinWait(64);
                continue;
            }

            if ((status & (Failed | BusError)) != 0)
            {
                WriteOffset(0x00, (byte)(status & StatusClear));
                throw Bus("SMBus transaction failed.");
            }

            if ((status & DeviceError) != 0)
            {
                WriteOffset(0x00, (byte)(status & StatusClear));
                return -1;
            }

            byte value = ReadOffset(0x05);
            if ((status & Interrupt) != 0)
            {
                WriteOffset(0x00, Interrupt);
            }

            return value;
        }

        WriteOffset(0x02, Kill);
        WaitIdle();
        WriteOffset(0x00, StatusClear);
        throw Bus("SMBus transaction timed out.");
    }

    private bool WaitIdle()
    {
        var wait = Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < 20)
        {
            if ((ReadOffset(0x00) & HostBusy) == 0)
            {
                return true;
            }

            Thread.SpinWait(64);
        }

        return false;
    }

    private void ReleaseSemaphore()
    {
        if (!_held)
        {
            return;
        }

        WriteOffset(0x08, (byte)(_semaphore | 0x20));
        _held = false;
    }

    private byte ReadOffset(byte offset) => PortIo.Read((ushort)(_base + offset));

    private void WriteOffset(byte offset, byte value) => PortIo.Write((ushort)(_base + offset), value);

    private static bool Present(ushort ioBase)
    {
        try
        {
            return PortIo.Read(ioBase) != 0xFF;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    private static IOException Bus(string detail)
    {
        var error = new IOException(detail);
        return error;
    }

    private static bool MachineHasAmdSmBus()
    {
        // Present-device list, not a config-space read. The I/O block is only touched once
        // this PCI id is actually on the machine.
        const int presentPci = 0x00000101;
        if (CM_Get_Device_ID_List_Size(out int chars, "PCI", presentPci) != 0 || chars <= 1)
        {
            return false;
        }

        var buffer = new char[chars];
        if (CM_Get_Device_ID_List("PCI", buffer, chars, presentPci) != 0)
        {
            return false;
        }

        return new string(buffer).Contains("VEN_1022&DEV_790B", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_Size(out int length, string? filter, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List(string? filter, char[] buffer, int length, int flags);

    private sealed class Release : IDisposable
    {
        private AmdSmBus? _bus;

        internal Release(AmdSmBus bus) => _bus = bus;

        public void Dispose()
        {
            _bus?.ReleaseSemaphore();
            _bus = null;
        }
    }
}

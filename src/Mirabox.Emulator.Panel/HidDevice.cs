using Microsoft.Win32.SafeHandles;
using Mirabox.Emulator.Core;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Mirabox.Emulator.Panel;

/// <summary>
/// Opens the driver's private device interface. The public HID descriptor is
/// kept byte-compatible with Stream Deck + and is used only by host software.
/// </summary>
internal sealed class HidDevice : IDisposable
{
    private const uint DigcfPresent = 0x02;
    private const uint DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x01;
    private const uint FileShareWrite = 0x02;
    private const uint OpenExisting = 3;
    private const uint IoctlInjectInput = 0x0022A000;
    private const uint IoctlGetCapture = 0x00226004;
    private const int CapturedHeaderLength = 4;
    private const int CapturedReportLength = CapturedHeaderLength + StreamDeckPlusProfile.OutputReportLength;

    private static readonly Guid PanelInterfaceGuid =
        new("2F5E3A9C-54A4-4A18-A73D-61F40EB0D92B");

    private readonly SafeFileHandle _handle;
    private readonly object _ioLock = new();

    private HidDevice(SafeFileHandle handle) => _handle = handle;

    public static HidDevice OpenStreamDeckPlus()
    {
        var interfaceGuid = PanelInterfaceGuid;
        var set = SetupDiGetClassDevs(ref interfaceGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1)) throw new Win32Exception();
        try
        {
            for (uint index = 0; ; index++)
            {
                var info = new SpDeviceInterfaceData { Size = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref interfaceGuid, index, ref info))
                {
                    if (Marshal.GetLastWin32Error() == 259) break;
                    continue;
                }

                SetupDiGetDeviceInterfaceDetail(set, ref info, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                var detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref info, detail, required, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                    if (path is null) continue;
                    var handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
                        IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                    if (!handle.IsInvalid) return new HidDevice(handle);
                    handle.Dispose();
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        throw new IOException("Виртуальный Stream Deck + (0FD9:0084) не найден. Установите драйвер и перезапустите панель.");
    }

    public void InjectInput(ReadOnlySpan<byte> report)
    {
        if (report.Length != StreamDeckPlusProfile.InputReportLength || report[0] != StreamDeckPlusProfile.InputReportId)
            throw new ArgumentException("Invalid Stream Deck + input report", nameof(report));
        var input = report.ToArray();
        lock (_ioLock)
        {
            if (!DeviceIoControl(_handle, IoctlInjectInput, input, input.Length, null, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось передать input report виртуальному HID");
        }
    }

    public bool TryReadCapture(out CapturedReport capture)
    {
        var output = new byte[CapturedReportLength];
        lock (_ioLock)
        {
            if (!DeviceIoControl(_handle, IoctlGetCapture, null, 0, output, output.Length, out var returned, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось прочитать служебный канал виртуального HID");
            if (returned != output.Length)
                throw new IOException($"Драйвер вернул {returned} байт вместо {output.Length}");
        }

        var kind = (CapturedReportKind)output[0];
        if (kind == CapturedReportKind.None)
        {
            capture = default;
            return false;
        }
        var length = BitConverter.ToUInt16(output, 2);
        var expected = kind switch
        {
            CapturedReportKind.Output => StreamDeckPlusProfile.OutputReportLength,
            CapturedReportKind.Feature => StreamDeckPlusProfile.FeatureReportLength,
            _ => throw new IOException($"Неизвестный тип перехваченного report: {(byte)kind}"),
        };
        if (length != expected)
            throw new IOException($"Драйвер вернул report длиной {length}, ожидалось {expected}");
        capture = new CapturedReport(kind, output.AsSpan(CapturedHeaderLength, length).ToArray());
        return true;
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int Size;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
        byte[]? inputBuffer, int inputLength, [Out] byte[]? outputBuffer, int outputLength,
        out int bytesReturned, IntPtr overlapped);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData, IntPtr detailData, uint detailDataSize,
        out uint requiredSize, IntPtr deviceInfoData);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}

internal enum CapturedReportKind : byte
{
    None = 0,
    Output = 1,
    Feature = 2,
}

internal readonly record struct CapturedReport(CapturedReportKind Kind, byte[] Data);

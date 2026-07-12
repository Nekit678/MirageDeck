using Microsoft.Win32.SafeHandles;
using Mirabox.Emulator.Core;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Mirabox.Emulator.Panel;

internal sealed class HidDevice : IDisposable
{
    private const uint DigcfPresent = 0x02;
    private const uint DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x01;
    private const uint FileShareWrite = 0x02;
    private const uint OpenExisting = 3;
    private const uint IoctlInjectInput = 0x00222000;
    private const uint IoctlGetOutput = 0x00222004;
    private static readonly Guid EmulatorInterfaceGuid = new("9A6C3D56-2683-4B22-9359-8FB4C38479B9");

    private readonly SafeFileHandle _handle;

    private HidDevice(SafeFileHandle handle) => _handle = handle;

    public static HidDevice OpenN4Pro()
    {
        var interfaceGuid = EmulatorInterfaceGuid;
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
                    if (handle.IsInvalid) { handle.Dispose(); continue; }
                    return new HidDevice(handle);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        throw new IOException("Виртуальный Mirabox N4 Pro не найден. Установите и запустите драйвер.");
    }

    public void InjectInput(ReadOnlySpan<byte> report)
    {
        if (report.Length != N4ProProfile.InputReportLength) throw new ArgumentException("Invalid input report", nameof(report));
        var input = report.ToArray();
        if (!DeviceIoControl(_handle, IoctlInjectInput, input, input.Length, null, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public bool TryReadOutput(out byte[] packet)
    {
        packet = new byte[N4ProProfile.OutputReportLength];
        if (!DeviceIoControl(_handle, IoctlGetOutput, null, 0, packet, packet.Length, out var received, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (received == packet.Length) return true;
        packet = [];
        return false;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
        byte[]? input, int inputLength, byte[]? output, int outputLength,
        out int bytesReturned, IntPtr overlapped);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr detailData, uint detailDataSize, out uint requiredSize, IntPtr deviceInfoData);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}

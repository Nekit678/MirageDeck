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
    private const int SidePayloadLength = 4 + 1 + 4 + N4ProProfile.OutputReportLength;
    private const int MinimumFeatureReportLength = 1 + SidePayloadLength;
    private const uint SideMagic = 0x4556424D;

    private readonly SafeFileHandle _handle;
    private readonly int _outputReportLength;
    private readonly int _featureReportLength;

    private HidDevice(SafeFileHandle handle, int outputReportLength, int featureReportLength)
    {
        _handle = handle;
        _outputReportLength = outputReportLength;
        _featureReportLength = featureReportLength;
    }

    public static HidDevice OpenN4Pro()
    {
        HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1)) throw new Win32Exception();
        try
        {
            for (uint index = 0; ; index++)
            {
                var info = new SpDeviceInterfaceData { Size = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref info))
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
                    var attributes = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
                    if (HidD_GetAttributes(handle, ref attributes)
                        && attributes.VendorId == N4ProProfile.VendorId
                        && attributes.ProductId == N4ProProfile.ProductId)
                    {
                        var (outputLength, featureLength) = GetReportLengths(handle);
                        if (outputLength < N4ProProfile.OutputReportLength + 1)
                        {
                            handle.Dispose();
                            throw new IOException(
                                $"Драйвер сообщил OutputReportByteLength={outputLength}, ожидалось не менее {N4ProProfile.OutputReportLength + 1}.");
                        }
                        if (featureLength < MinimumFeatureReportLength)
                        {
                            handle.Dispose();
                            throw new IOException(
                                $"Драйвер сообщил FeatureReportByteLength={featureLength}, ожидалось не менее {MinimumFeatureReportLength}.");
                        }
                        return new HidDevice(handle, outputLength, featureLength);
                    }
                    handle.Dispose();
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        throw new IOException("Виртуальный Mirabox N4 Pro Global (5548:1021) не найден.");
    }

    public void InjectInput(ReadOnlySpan<byte> report)
    {
        if (report.Length != N4ProProfile.InputReportLength)
            throw new ArgumentException("Invalid input report", nameof(report));
        var output = new byte[_outputReportLength];
        output[0] = 0;
        BitConverter.TryWriteBytes(output.AsSpan(1, 4), SideMagic);
        output[5] = 1;
        report.CopyTo(output.AsSpan(6));
        if (!HidD_SetOutputReport(_handle, output, output.Length))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error,
                $"HidD_SetOutputReport завершился ошибкой {error} (buffer={output.Length})");
        }
    }

    public bool TryReadOutput(out byte[] packet)
    {
        var feature = new byte[_featureReportLength];
        feature[0] = 0;
        if (!HidD_GetFeature(_handle, feature, feature.Length))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error,
                $"HidD_GetFeature завершился ошибкой {error} (buffer={feature.Length})");
        }
        if (BitConverter.ToUInt32(feature, 1) != SideMagic || feature[5] != 2)
        {
            packet = [];
            return false;
        }
        packet = feature.AsSpan(10, N4ProProfile.OutputReportLength).ToArray();
        return true;
    }

    private static (int Output, int Feature) GetReportLengths(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out var preparsedData))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "HidD_GetPreparsedData завершился ошибкой");
        var caps = Marshal.AllocHGlobal(64);
        try
        {
            Marshal.Copy(new byte[64], 0, caps, 64);
            var status = HidP_GetCaps(preparsedData, caps);
            if (status < 0) throw new IOException($"HidP_GetCaps завершился с NTSTATUS 0x{status:X8}.");
            return (
                (ushort)Marshal.ReadInt16(caps, 6),
                (ushort)Marshal.ReadInt16(caps, 8));
        }
        finally
        {
            Marshal.FreeHGlobal(caps);
            HidD_FreePreparsedData(preparsedData);
        }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HiddAttributes attributes);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsedData);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);
    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, IntPtr capabilities);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] reportBuffer, int reportBufferLength);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_SetOutputReport(SafeFileHandle handle, byte[] reportBuffer, int reportBufferLength);

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

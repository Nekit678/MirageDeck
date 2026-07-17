using Microsoft.Win32.SafeHandles;
using Mirabox.Emulator.Core;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Mirabox.Emulator.Panel;

/// <summary>Opens the public HID collection and uses its panel-only feature report.</summary>
internal sealed class HidDevice : IDisposable
{
    private const uint DigcfPresent = 0x02;
    private const uint DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x01;
    private const uint FileShareWrite = 0x02;
    private const uint OpenExisting = 3;

    private readonly SafeFileHandle _handle;
    private readonly object _ioLock = new();
    private byte _inputTransaction;

    private HidDevice(SafeFileHandle handle)
    {
        _handle = handle;
        ResetPanelTransport();
    }

    public static HidDevice OpenStreamDeckPlus()
    {
        HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1)) throw new Win32Exception();
        var openErrors = new List<int>();
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
                    if (handle.IsInvalid)
                    {
                        openErrors.Add(Marshal.GetLastWin32Error());
                        handle.Dispose();
                        continue;
                    }

                    var attributes = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
                    if (HidD_GetAttributes(handle, ref attributes)
                        && attributes.VendorId == StreamDeckPlusProfile.VendorId
                        && attributes.ProductId == StreamDeckPlusProfile.ProductId)
                    {
                        var featureLength = GetFeatureReportLength(handle);
                        if (featureLength != PanelTransportProtocol.ReportLength)
                        {
                            handle.Dispose();
                            throw new IOException(
                                $"Драйвер сообщил FeatureReportByteLength={featureLength}, ожидалось {PanelTransportProtocol.ReportLength}.");
                        }
                        try { return new HidDevice(handle); }
                        catch { handle.Dispose(); throw; }
                    }
                    handle.Dispose();
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }

        var suffix = openErrors.Count == 0
            ? string.Empty
            : $" Последняя ошибка CreateFile: {new Win32Exception(openErrors[^1]).Message} ({openErrors[^1]}).";
        throw new IOException(
            $"Виртуальный Stream Deck + (0FD9:0084) не найден среди публичных HID-устройств.{suffix}");
    }

    public void InjectInput(ReadOnlySpan<byte> report)
    {
        var transaction = unchecked(++_inputTransaction);
        var chunks = PanelTransportProtocol.CreateInjectionReports(report, transaction);
        lock (_ioLock)
        {
            foreach (var chunk in chunks) SetFeature(chunk, "Не удалось передать input report виртуальному HID");
        }
    }

    public bool TryReadCapture(out CapturedReport capture)
    {
        lock (_ioLock)
        {
            var first = ReadPanelFeature();
            if (first.Command == PanelTransportCommand.None)
            {
                capture = default;
                return false;
            }
            if (first.Index != 0)
                throw new IOException($"Служебная передача началась с чанка {first.Index}, ожидался 0");

            var contents = new byte[first.TotalLength];
            CopyChunk(first, contents);
            for (byte expected = 1; expected < first.Count; expected++)
            {
                var next = ReadPanelFeature();
                if (next.Command != PanelTransportCommand.CaptureChunk
                    || next.Transaction != first.Transaction
                    || next.Kind != first.Kind
                    || next.Index != expected
                    || next.Count != first.Count
                    || next.TotalLength != first.TotalLength)
                    throw new IOException($"Нарушена последовательность служебных HID-чанков на индексе {expected}");
                CopyChunk(next, contents);
            }

            capture = new CapturedReport(first.Kind, contents);
            return true;
        }
    }

    private void ResetPanelTransport()
    {
        lock (_ioLock) SetFeature(PanelTransportProtocol.CreateResetReport(), "Не удалось инициализировать служебный HID-канал");
    }

    private PanelTransportChunk ReadPanelFeature()
    {
        var feature = new byte[PanelTransportProtocol.ReportLength];
        feature[0] = PanelTransportProtocol.ReportId;
        if (!HidD_GetFeature(_handle, feature, feature.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось прочитать служебный HID Feature report");
        return PanelTransportProtocol.ParseResponse(feature);
    }

    private void SetFeature(byte[] feature, string message)
    {
        if (!HidD_SetFeature(_handle, feature, feature.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), message);
    }

    private static void CopyChunk(PanelTransportChunk chunk, byte[] destination) =>
        chunk.Data.CopyTo(destination, chunk.Index * PanelTransportProtocol.PayloadLength);

    private static int GetFeatureReportLength(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out var preparsedData))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "HidD_GetPreparsedData завершился ошибкой");
        var caps = Marshal.AllocHGlobal(64);
        try
        {
            Marshal.Copy(new byte[64], 0, caps, 64);
            var status = HidP_GetCaps(preparsedData, caps);
            if (status < 0) throw new IOException($"HidP_GetCaps завершился с NTSTATUS 0x{status:X8}.");
            return (ushort)Marshal.ReadInt16(caps, 8);
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
    private static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] reportBuffer, int reportBufferLength);

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
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}

internal readonly record struct CapturedReport(PanelCaptureKind Kind, byte[] Data);

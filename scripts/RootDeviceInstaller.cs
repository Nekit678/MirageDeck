using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Mirabox.Emulator.Install
{
    public static class RootDeviceInstaller
    {
        private const uint DICD_GENERATE_ID = 0x00000001;
        private const uint SPDRP_HARDWAREID = 0x00000001;
        private const uint DIF_REGISTERDEVICE = 0x00000019;
        private const uint INSTALLFLAG_FORCE = 0x00000001;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public UIntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetINFClass(
            string infName,
            out Guid classGuid,
            StringBuilder className,
            uint classNameSize,
            out uint requiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiCreateDeviceInfoList(
            ref Guid classGuid,
            IntPtr hwndParent);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiCreateDeviceInfo(
            IntPtr deviceInfoSet,
            string deviceName,
            ref Guid classGuid,
            string deviceDescription,
            IntPtr hwndParent,
            uint creationFlags,
            ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiSetDeviceRegistryProperty(
            IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData,
            uint property,
            byte[] propertyBuffer,
            uint propertyBufferSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiCallClassInstaller(
            uint installFunction,
            IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateDriverForPlugAndPlayDevices(
            IntPtr hwndParent,
            string hardwareId,
            string fullInfPath,
            uint installFlags,
            [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);

        public static bool Install(string infPath, string hardwareId)
        {
            infPath = Path.GetFullPath(infPath);
            if (!File.Exists(infPath))
            {
                throw new FileNotFoundException("The driver INF was not found.", infPath);
            }

            Guid classGuid;
            uint requiredSize;
            StringBuilder className = new StringBuilder(256);
            if (!SetupDiGetINFClass(infPath, out classGuid, className, 256, out requiredSize))
            {
                ThrowLastError("SetupDiGetINFClass");
            }

            IntPtr deviceInfoSet = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
            if (deviceInfoSet == InvalidHandleValue)
            {
                ThrowLastError("SetupDiCreateDeviceInfoList");
            }

            try
            {
                SP_DEVINFO_DATA deviceInfoData = new SP_DEVINFO_DATA();
                deviceInfoData.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                if (!SetupDiCreateDeviceInfo(
                    deviceInfoSet,
                    className.ToString(),
                    ref classGuid,
                    null,
                    IntPtr.Zero,
                    DICD_GENERATE_ID,
                    ref deviceInfoData))
                {
                    ThrowLastError("SetupDiCreateDeviceInfo");
                }

                byte[] hardwareIds = Encoding.Unicode.GetBytes(hardwareId + "\0\0");
                if (!SetupDiSetDeviceRegistryProperty(
                    deviceInfoSet,
                    ref deviceInfoData,
                    SPDRP_HARDWAREID,
                    hardwareIds,
                    (uint)hardwareIds.Length))
                {
                    ThrowLastError("SetupDiSetDeviceRegistryProperty");
                }

                if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, deviceInfoSet, ref deviceInfoData))
                {
                    ThrowLastError("SetupDiCallClassInstaller(DIF_REGISTERDEVICE)");
                }

                bool rebootRequired;
                if (!UpdateDriverForPlugAndPlayDevices(
                    IntPtr.Zero,
                    hardwareId,
                    infPath,
                    INSTALLFLAG_FORCE,
                    out rebootRequired))
                {
                    ThrowLastError("UpdateDriverForPlugAndPlayDevices");
                }
                return rebootRequired;
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }
        }

        private static void ThrowLastError(string operation)
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, operation + " failed");
        }
    }
}

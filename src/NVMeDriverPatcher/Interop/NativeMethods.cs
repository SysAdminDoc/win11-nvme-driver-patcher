using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NVMeDriverPatcher.Interop;

/// <summary>
/// P/Invoke declarations for NVMe device access, IOCTL operations, and device management.
/// </summary>
internal static partial class NativeMethods
{
    // ========================================================================
    // kernel32.dll - File/Device Handle Operations
    // ========================================================================

    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    /// <summary>
    /// Opens a handle to a device (e.g., \\.\PhysicalDriveN) for IOCTL operations.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    /// <summary>
    /// Sends a control code directly to a device driver, causing the corresponding device to perform the corresponding operation.
    /// Used for IOCTL_STORAGE_QUERY_PROPERTY to retrieve NVMe SMART/Health data.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref byte lpInBuffer,
        uint nInBufferSize,
        ref byte lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    // ========================================================================
    // setupapi.dll - Device Enumeration
    // ========================================================================

    internal const uint DIGCF_PRESENT = 0x00000002;
    internal const uint DIGCF_ALLCLASSES = 0x00000004;
    internal const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    internal static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    internal const uint DIF_PROPERTYCHANGE = 0x00000012;
    internal const uint DICS_FLAG_GLOBAL = 0x00000001;
    internal const uint DICS_PROPCHANGE = 0x00000003;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_CLASSINSTALL_HEADER
    {
        public uint cbSize;
        public uint InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_PROPCHANGE_PARAMS
    {
        public SP_CLASSINSTALL_HEADER ClassInstallHeader;
        public uint StateChange;
        public uint Scope;
        public uint HwProfile;
    }

    /// <summary>
    /// Returns a handle to a device information set that contains requested device information elements.
    /// </summary>
    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    internal static partial IntPtr SetupDiGetClassDevs(
        in Guid classGuid,
        [MarshalAs(UnmanagedType.LPWStr)] string? enumerator,
        IntPtr hwndParent,
        uint flags);

    /// <summary>
    /// Returns a handle to a device information set for all classes.
    /// </summary>
    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    internal static partial IntPtr SetupDiGetClassDevsAllClasses(
        IntPtr classGuid,
        [MarshalAs(UnmanagedType.LPWStr)] string? enumerator,
        IntPtr hwndParent,
        uint flags);

    /// <summary>
    /// Enumerates device information elements in a device information set.
    /// </summary>
    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref SP_DEVINFO_DATA deviceInfoData);

    /// <summary>
    /// Sets class install parameters for a device information element.
    /// </summary>
    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiSetClassInstallParams(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        ref SP_PROPCHANGE_PARAMS classInstallParams,
        uint classInstallParamsSize);

    /// <summary>
    /// Invokes the appropriate class installer, and any registered co-installers, for the specified device.
    /// </summary>
    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiCallClassInstaller(
        uint installFunction,
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData);

    /// <summary>
    /// Destroys a device information set and frees all associated memory.
    /// </summary>
    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiDestroyDeviceInfoList(
        IntPtr deviceInfoSet);

    // ========================================================================
    // cfgmgr32.dll - Configuration Manager
    // ========================================================================

    internal const uint CM_REENUMERATE_NORMAL = 0x00000000;
    internal const uint CM_REENUMERATE_SYNCHRONOUS = 0x00000001;
    internal const uint CR_SUCCESS = 0x00000000;

    /// <summary>
    /// Re-enumerates a device node and its children, forcing the PnP manager to re-detect devices.
    /// Used during hot-swap to restart the NVMe controller after driver changes.
    /// </summary>
    [LibraryImport("cfgmgr32.dll")]
    internal static partial uint CM_Reenumerate_DevNode(
        uint dnDevInst,
        uint ulFlags);

    /// <summary>
    /// Locates a device instance in the device tree by its device instance ID.
    /// </summary>
    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint CM_Locate_DevNode(
        out uint pdnDevInst,
        string? pDeviceID,
        uint ulFlags);

    internal const uint CM_LOCATE_DEVNODE_NORMAL = 0x00000000;
}

using System.Runtime.InteropServices;

namespace NVMeDriverPatcher.Interop;

/// <summary>
/// IOCTL constants and structs for querying NVMe device properties via DeviceIoControl.
/// Based on Windows ntddstor.h / winioctl.h definitions.
/// </summary>
internal static class StorageStructs
{
    // ========================================================================
    // IOCTL Constants
    // ========================================================================

    internal const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    // STORAGE_PROPERTY_ID values
    internal const int StorageAdapterProtocolSpecificProperty = 49;
    internal const int StorageDeviceProtocolSpecificProperty = 50;

    // STORAGE_QUERY_TYPE
    internal const int PropertyStandardQuery = 0;

    // STORAGE_PROTOCOL_TYPE
    internal const int ProtocolTypeNvme = 3;

    // STORAGE_PROTOCOL_NVME_DATA_TYPE
    internal const int NVMeDataTypeIdentify = 1;
    internal const int NVMeDataTypeLogPage = 2;
    internal const int NVMeDataTypeFeature = 3;

    // NVMe Log Page IDs
    internal const int NVME_LOG_PAGE_ERROR_INFO = 0x01;
    internal const int NVME_LOG_PAGE_HEALTH_INFO = 0x02;
    internal const int NVME_LOG_PAGE_FIRMWARE_SLOT_INFO = 0x03;

    // ========================================================================
    // STORAGE_PROPERTY_QUERY (input buffer for DeviceIoControl)
    // ========================================================================

    /// <summary>
    /// Input structure for IOCTL_STORAGE_QUERY_PROPERTY.
    /// PropertyId + QueryType + AdditionalParameters (protocol-specific data).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;       // STORAGE_PROPERTY_ID
        public int QueryType;        // STORAGE_QUERY_TYPE
        // AdditionalParameters[1] follows - we embed STORAGE_PROTOCOL_SPECIFIC_DATA here
    }

    // ========================================================================
    // STORAGE_PROTOCOL_SPECIFIC_DATA (embedded in AdditionalParameters)
    // ========================================================================

    /// <summary>
    /// Describes the protocol-specific data for an NVMe query.
    /// Appended after STORAGE_PROPERTY_QUERY as AdditionalParameters.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct STORAGE_PROTOCOL_SPECIFIC_DATA
    {
        public int ProtocolType;                // STORAGE_PROTOCOL_TYPE (3 = NVMe)
        public int DataType;                    // STORAGE_PROTOCOL_NVME_DATA_TYPE
        public int ProtocolDataRequestValue;    // Log Page ID (0x02 for SMART)
        public int ProtocolDataRequestSubValue; // Log-specific sub-value (CDW10 bits 15:0)
        public int ProtocolDataOffset;          // Offset from start of this struct to data
        public int ProtocolDataLength;          // Length of returned protocol data
        public int FixedProtocolReturnData;     // Protocol-specific fixed return data
        public int ProtocolDataRequestSubValue2; // Additional sub-value (CDW10 bits 31:16)
        public int ProtocolDataRequestSubValue3; // CDW11
        public int ProtocolDataRequestSubValue4; // CDW12
        public int ProtocolDataRequestSubValue5; // CDW13
    }

    // ========================================================================
    // Combined input buffer: STORAGE_PROPERTY_QUERY + STORAGE_PROTOCOL_SPECIFIC_DATA
    // ========================================================================

    /// <summary>
    /// Combined input buffer for NVMe protocol-specific queries.
    /// This is STORAGE_PROPERTY_QUERY with STORAGE_PROTOCOL_SPECIFIC_DATA as AdditionalParameters.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct STORAGE_QUERY_BUFFER
    {
        public int PropertyId;
        public int QueryType;
        public STORAGE_PROTOCOL_SPECIFIC_DATA ProtocolSpecific;
    }

    // ========================================================================
    // STORAGE_PROTOCOL_DATA_DESCRIPTOR (output buffer header)
    // ========================================================================

    /// <summary>
    /// Output header from IOCTL_STORAGE_QUERY_PROPERTY for protocol-specific queries.
    /// The actual NVMe data follows at ProtocolSpecificData.ProtocolDataOffset from the start of ProtocolSpecificData.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct STORAGE_PROTOCOL_DATA_DESCRIPTOR
    {
        public int Version;
        public int Size;
        public STORAGE_PROTOCOL_SPECIFIC_DATA ProtocolSpecificData;
    }

    // ========================================================================
    // NVME_HEALTH_INFO_LOG (NVMe Spec 1.4+ Log Page 02h - 512 bytes)
    // ========================================================================

    /// <summary>
    /// NVMe SMART / Health Information Log (Log Page 02h).
    /// 512-byte structure per NVMe Base Specification.
    /// All multi-byte fields are little-endian as stored by the controller.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 512)]
    internal struct NVME_HEALTH_INFO_LOG
    {
        /// <summary>Byte 0: Critical Warning (bit flags)</summary>
        public byte CriticalWarning;

        /// <summary>Bytes 1-2: Composite Temperature in Kelvin</summary>
        public ushort CompositeTemperature;

        /// <summary>Byte 3: Available Spare (0-100%)</summary>
        public byte AvailableSpare;

        /// <summary>Byte 4: Available Spare Threshold (0-100%)</summary>
        public byte AvailableSpareThreshold;

        /// <summary>Byte 5: Percentage Used (0-255, can exceed 100%)</summary>
        public byte PercentageUsed;

        /// <summary>Byte 6: Endurance Group Critical Warning Summary</summary>
        public byte EnduranceGroupCriticalWarningSummary;

        /// <summary>Bytes 7-31: Reserved</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)]
        public byte[] Reserved0;

        /// <summary>Bytes 32-47: Data Units Read (128-bit, in 512-byte units * 1000)</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] DataUnitsRead;

        /// <summary>Bytes 48-63: Data Units Written (128-bit, in 512-byte units * 1000)</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] DataUnitsWritten;

        /// <summary>Bytes 64-79: Host Read Commands (128-bit)</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] HostReadCommands;

        /// <summary>Bytes 80-95: Host Write Commands (128-bit)</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] HostWriteCommands;

        /// <summary>Bytes 96-111: Controller Busy Time in minutes (128-bit)</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ControllerBusyTime;

        /// <summary>Bytes 112-127: Power Cycles (128-bit)</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] PowerCycles;

        /// <summary>Bytes 128-143: Power On Hours (128-bit)</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] PowerOnHours;

        /// <summary>Bytes 144-159: Unsafe Shutdowns (128-bit)</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] UnsafeShutdowns;

        /// <summary>Bytes 160-175: Media and Data Integrity Errors (128-bit)</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] MediaErrors;

        /// <summary>Bytes 176-191: Number of Error Information Log Entries (128-bit)</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ErrorLogEntries;

        /// <summary>Bytes 192-195: Warning Composite Temperature Time (minutes)</summary>
        public uint WarningCompositeTemperatureTime;

        /// <summary>Bytes 196-199: Critical Composite Temperature Time (minutes)</summary>
        public uint CriticalCompositeTemperatureTime;

        /// <summary>Bytes 200-201: Temperature Sensor 1 (Kelvin, 0 = not implemented)</summary>
        public ushort TemperatureSensor1;

        /// <summary>Bytes 202-203: Temperature Sensor 2</summary>
        public ushort TemperatureSensor2;

        /// <summary>Bytes 204-205: Temperature Sensor 3</summary>
        public ushort TemperatureSensor3;

        /// <summary>Bytes 206-207: Temperature Sensor 4</summary>
        public ushort TemperatureSensor4;

        /// <summary>Bytes 208-209: Temperature Sensor 5</summary>
        public ushort TemperatureSensor5;

        /// <summary>Bytes 210-211: Temperature Sensor 6</summary>
        public ushort TemperatureSensor6;

        /// <summary>Bytes 212-213: Temperature Sensor 7</summary>
        public ushort TemperatureSensor7;

        /// <summary>Bytes 214-215: Temperature Sensor 8</summary>
        public ushort TemperatureSensor8;

        /// <summary>Bytes 216-219: Thermal Management Temperature 1 Transition Count</summary>
        public uint ThermalMgmtTemp1TransitionCount;

        /// <summary>Bytes 220-223: Thermal Management Temperature 2 Transition Count</summary>
        public uint ThermalMgmtTemp2TransitionCount;

        /// <summary>Bytes 224-227: Total Time For Thermal Management Temperature 1</summary>
        public uint TotalTimeForThermalMgmtTemp1;

        /// <summary>Bytes 228-231: Total Time For Thermal Management Temperature 2</summary>
        public uint TotalTimeForThermalMgmtTemp2;

        /// <summary>Bytes 232-511: Reserved</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 280)]
        public byte[] Reserved1;
    }

    // ========================================================================
    // Helper: Build combined input buffer for NVMe SMART log query
    // ========================================================================

    /// <summary>
    /// Creates the marshaled input byte array for an NVMe SMART/Health log page query.
    /// </summary>
    internal static byte[] BuildSmartLogQueryBuffer()
    {
        var query = new STORAGE_QUERY_BUFFER
        {
            PropertyId = StorageDeviceProtocolSpecificProperty,
            QueryType = PropertyStandardQuery,
            ProtocolSpecific = new STORAGE_PROTOCOL_SPECIFIC_DATA
            {
                ProtocolType = ProtocolTypeNvme,
                DataType = NVMeDataTypeLogPage,
                ProtocolDataRequestValue = NVME_LOG_PAGE_HEALTH_INFO,
                ProtocolDataRequestSubValue = 0,
                ProtocolDataOffset = Marshal.SizeOf<STORAGE_PROTOCOL_SPECIFIC_DATA>(),
                ProtocolDataLength = 512 // NVME_HEALTH_INFO_LOG is 512 bytes
            }
        };

        int size = Marshal.SizeOf<STORAGE_QUERY_BUFFER>();
        byte[] buffer = new byte[size];
        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(query, handle.AddrOfPinnedObject(), false);
        }
        finally
        {
            handle.Free();
        }
        return buffer;
    }
}

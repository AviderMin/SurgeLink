using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SurgeLink.Protocols.Ssh.Internal;

/// <summary>Checks the client side of an accepted IPv4 loopback connection on Windows.</summary>
internal static class LoopbackConnectionOwner
{
    private const uint InsufficientBuffer = 122;
    private const int OwnerPidConnections = 4;
    private const uint Established = 5;
    private const int RowSize = 6 * sizeof(uint);
    private const uint LoopbackAddress = 0x0100007F;

    internal static bool IsOwnedBy(uint clientPort, uint listenerPort, int processId)
    {
        if (!OperatingSystem.IsWindows()) { return false; }

        var size = 0;
        byte[]? buffer = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var result = GetExtendedTcpTable(buffer, ref size, false, AddressFamily.InterNetwork, OwnerPidConnections, 0);
            if (result == 0 && buffer is not null)
                return ContainsOwner(buffer, clientPort, listenerPort, (uint)processId);
            if (result != InsufficientBuffer || size < sizeof(uint)) { return false; }
            buffer = new byte[size];
        }
        return false;
    }

    private static bool ContainsOwner(ReadOnlySpan<byte> buffer, uint clientPort, uint listenerPort, uint processId)
    {
        var count = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (count > (buffer.Length - sizeof(uint)) / RowSize) { return false; }

        // MIB_TCPROW_OWNER_PID stores six DWORDs. Addresses and ports are in network byte order.
        for (var index = 0; index < count; index++)
        {
            var row = buffer.Slice(sizeof(uint) + index * RowSize, RowSize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(row) == Established
                && BinaryPrimitives.ReadUInt32LittleEndian(row[4..]) == LoopbackAddress
                && BinaryPrimitives.ReadUInt16BigEndian(row[8..]) == clientPort
                && BinaryPrimitives.ReadUInt32LittleEndian(row[12..]) == LoopbackAddress
                && BinaryPrimitives.ReadUInt16BigEndian(row[16..]) == listenerPort
                && BinaryPrimitives.ReadUInt32LittleEndian(row[20..]) == processId)
            {
                return true;
            }
        }
        return false;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetExtendedTcpTable([Out] byte[]? table, ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool sort, AddressFamily addressFamily, int tableClass, uint reserved);
}

namespace ConnectionClue.Windows;

/// <summary>Frames this PC's Wi-Fi radio sent since it connected: all, those that needed another attempt, and those that failed.</summary>
public sealed record WifiFrameCounts(long Sent, long Retried, long Failed);

/// <summary>What the radio reports about the current link without naming the network: channel, signal in dBm, frame counts.
/// Any part the driver or Windows does not provide is null.</summary>
public sealed record WifiLink(int? Channel, int? SignalDbm, WifiFrameCounts? Frames);

/// <summary>Read-only WLAN facts that the WinRT Wi-Fi API does not expose.</summary>
public static unsafe class WlanInfo
{
    // WLAN_STATISTICS: three 8-byte counters, two 96-byte MAC counter blocks, the PHY count (padded to 8), then one
    // 144-byte block of 18 counters per PHY. A PHY block starts with transmitted, multicast, failed and retry counts.
    private const int PhyCountOffset = 24 + 2 * 96, FirstPhyOffset = PhyCountOffset + 8, PhyBlockSize = 18 * 8;

    /// <summary>
    /// Channel, signal strength and frame statistics of the connected radio. Unlike the connection details (SSID, BSSID, PHY
    /// type), Windows 11 hands these out without the Location permission, so they stay readable on every PC.
    /// </summary>
    public static WifiLink? ReadLink(Guid interfaceGuid)
    {
        uint negotiated;
        HANDLE client;
        if (PInvoke.WlanOpenHandle(2, null, &negotiated, &client) != 0) return null;
        try
        {
            int? channel = Query(client, interfaceGuid, WLAN_INTF_OPCODE.wlan_intf_opcode_channel_number) is { Length: >= 4 } c
                && BitConverter.ToInt32(c) is > 0 and var number ? number : null;
            int? dbm = Query(client, interfaceGuid, WLAN_INTF_OPCODE.wlan_intf_opcode_rssi) is { Length: >= 4 } r
                && BitConverter.ToInt32(r) is >= -120 and <= 0 and var level ? level : null;
            var frames = Query(client, interfaceGuid, WLAN_INTF_OPCODE.wlan_intf_opcode_statistics) is { } s ? ParseStatistics(s) : null;
            return channel is null && dbm is null && frames is null ? null : new(channel, dbm, frames);
        }
        finally
        {
            _ = PInvoke.WlanCloseHandle(client, null);
        }
    }

    /// <summary>Sums the transmitted, retried and failed frame counts over every PHY the driver reports.</summary>
    public static WifiFrameCounts? ParseStatistics(ReadOnlySpan<byte> data)
    {
        if (data.Length < FirstPhyOffset) return null;
        uint phys = BitConverter.ToUInt32(data.Slice(PhyCountOffset, 4));
        long sent = 0, retried = 0, failed = 0;
        for (int i = 0; i < phys && FirstPhyOffset + (i + 1) * PhyBlockSize <= data.Length; i++)
        {
            var phy = data[(FirstPhyOffset + i * PhyBlockSize)..];
            sent += Count(phy, 0);
            failed += Count(phy, 16);
            retried += Count(phy, 24);
        }
        return new(sent, retried, failed);
    }

    private static long Count(ReadOnlySpan<byte> block, int offset) =>
        (long)Math.Min(BitConverter.ToUInt64(block.Slice(offset, 8)), long.MaxValue);

    private static byte[]? Query(HANDLE client, Guid interfaceGuid, WLAN_INTF_OPCODE opcode)
    {
        uint size;
        void* data;
        WLAN_OPCODE_VALUE_TYPE type;
        if (PInvoke.WlanQueryInterface(client, &interfaceGuid, opcode, null, &size, &data, &type) != 0 || data is null) return null;
        try { return new ReadOnlySpan<byte>(data, (int)Math.Min(size, 1 << 20)).ToArray(); }
        finally { PInvoke.WlanFreeMemory(data); }
    }

    /// <summary>
    /// BSSID ("aa:bb:cc:dd:ee:ff") of the access point the interface is connected to, or null. With band steering one
    /// network name spans several bands, so only the BSSID identifies the connected radio and its channel.
    /// </summary>
    public static string? ConnectedBssid(Guid interfaceGuid)
    {
        uint negotiated;
        HANDLE client;
        if (PInvoke.WlanOpenHandle(2, null, &negotiated, &client) != 0) return null;
        try
        {
            uint size;
            void* data;
            WLAN_OPCODE_VALUE_TYPE type;
            if (PInvoke.WlanQueryInterface(client, &interfaceGuid, WLAN_INTF_OPCODE.wlan_intf_opcode_current_connection, null,
                &size, &data, &type) != 0 || data is null) return null;
            try
            {
                if (size < sizeof(WLAN_CONNECTION_ATTRIBUTES)) return null;
                var connection = (WLAN_CONNECTION_ATTRIBUTES*)data;
                if (connection->isState != WLAN_INTERFACE_STATE.wlan_interface_state_connected) return null;
                var bssid = connection->wlanAssociationAttributes.dot11Bssid;
                var text = new System.Text.StringBuilder(17);
                for (int i = 0; i < 6; i++) text.Append(i == 0 ? "" : ":").Append(bssid[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return text.ToString();
            }
            finally
            {
                PInvoke.WlanFreeMemory(data);
            }
        }
        finally
        {
            _ = PInvoke.WlanCloseHandle(client, null);
        }
    }
}

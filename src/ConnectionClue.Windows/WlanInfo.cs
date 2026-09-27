namespace ConnectionClue.Windows;

/// <summary>Read-only WLAN facts that the WinRT Wi-Fi API does not expose.</summary>
public static unsafe class WlanInfo
{
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

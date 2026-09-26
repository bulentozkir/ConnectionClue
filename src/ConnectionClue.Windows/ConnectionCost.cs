using global::Windows.Networking.Connectivity;

namespace ConnectionClue.Windows;

public static class ConnectionCost
{
    /// <summary>
    /// True on cellular (WWAN) and on connections Windows treats as metered, such as phone hotspots, so scheduled checks
    /// never spend mobile data unless the user opts in. Unknown counts as mobile for the same reason.
    /// </summary>
    public static bool IsMobileNetwork()
    {
        try
        {
            if (NetworkInformation.GetInternetConnectionProfile() is { } internet)
                return internet.IsWwanConnectionProfile || IsMetered(internet.GetConnectionCost());
            return NetworkInformation.GetConnectionProfiles().Any(profile =>
                profile.IsWwanConnectionProfile && profile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>True on metered or near-cap connections, where data-heavy tests must not run without asking.</summary>
    public static bool IsMetered()
    {
        try
        {
            return IsMetered(NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost());
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return true; // unknown cost: be conservative
        }
    }

    private static bool IsMetered(global::Windows.Networking.Connectivity.ConnectionCost? cost) =>
        cost is not null && (cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable
            || cost.ApproachingDataLimit || cost.OverDataLimit || cost.Roaming);
}

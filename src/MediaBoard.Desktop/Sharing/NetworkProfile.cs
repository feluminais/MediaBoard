namespace MediaBoard.Desktop.Sharing;

/// <summary>Reads how Windows has classed this PC's connection, through the Network List Manager.</summary>
public static class NetworkProfile
{
    private const int ConnectedNetworks = 1; // NLM_ENUM_NETWORK_CONNECTED
    private const int PublicCategory = 0; // NLM_NETWORK_CATEGORY_PUBLIC

    private static readonly Guid NetworkListManager = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    /// <summary>
    /// True when a network with internet access is Public. Virtual adapters are Public with no internet, and warning
    /// about those would be wrong.
    /// </summary>
    public static bool IsPublic()
    {
        dynamic manager = Activator.CreateInstance(Type.GetTypeFromCLSID(NetworkListManager, throwOnError: true)!)!;
        foreach (var network in manager.GetNetworks(ConnectedNetworks))
        {
            if (network.IsConnectedToInternet && network.GetCategory() == PublicCategory) return true;
        }

        return false;
    }
}

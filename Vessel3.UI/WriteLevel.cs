using Vessel3.Client;
using Vessel3.Primitives;

namespace Vessel3.UI;

internal static class WriteLevel
{
    public const string Ingest = "ingest";
    public const string Mirror = "mirror";
    public const string Vault = "vault";
    public const string ReadOnly = "read-only";
    public const string Remote = "remote";

    public static string Of(IReadOnlyList<string> capabilities)
    {
        if (Has(capabilities, VesselVolumeCapabilities.Remote)) return Remote;
        var readOnly = Has(capabilities, VesselVolumeCapabilities.ReadOnly);
        var mirrored = Has(capabilities, VesselVolumeCapabilities.Mirrored);
        if (readOnly && mirrored && Has(capabilities, VesselVolumeCapabilities.OnDemand)) return Vault;
        if (!readOnly && Has(capabilities, VesselVolumeCapabilities.Ingest)) return Ingest;
        if (mirrored) return Mirror;
        return ReadOnly;
    }

    public static string OfNode(ClusterPeerDto node)
    {
        var best = ReadOnly;
        foreach (var volume in node.Volumes)
        {
            var level = Of(volume.Capabilities);
            if (Rank(level) > Rank(best)) best = level;
        }
        return best;
    }

    public static string Badge(string level) => level switch
    {
        Ingest => "badge-mint",
        Mirror => "badge-accent",
        Vault => "badge-warning",
        Remote => "badge-accent",
        _ => "badge-neutral"
    };

    private static int Rank(string level) => level switch
    {
        Ingest => 3,
        Mirror => 2,
        Vault => 1,
        _ => 0
    };

    private static bool Has(IReadOnlyList<string> capabilities, string target)
    {
        foreach (var capability in capabilities)
        {
            if (string.Equals(capability, target, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

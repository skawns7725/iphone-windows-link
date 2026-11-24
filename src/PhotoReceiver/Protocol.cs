using System.Net;
using System.Text.Json;

namespace PhotoReceiver;

public record DeviceInfo(
    string Alias, string? DeviceModel, string? DeviceType, string Fingerprint,
    string Version = "2.0", int Port = 53317, string Protocol = "https", bool Download = false);

public record DiscoveryPacket(
    string Alias, string? DeviceModel, string? DeviceType, string Fingerprint,
    string Version, int Port, string Protocol, bool Download, bool Announce);

public static class DiscoveryProtocol
{
    public const int Port = 53317;
    public static readonly IPEndPoint Multicast = new(IPAddress.Parse("224.0.0.167"), Port);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static byte[] Encode(DeviceInfo device, bool announce) =>
        JsonSerializer.SerializeToUtf8Bytes(new DiscoveryPacket(
            device.Alias, device.DeviceModel, device.DeviceType, device.Fingerprint,
            device.Version, device.Port, device.Protocol, device.Download, announce), Json);

    public static DiscoveryPacket? Parse(ReadOnlySpan<byte> datagram, string localFingerprint)
    {
        try
        {
            var packet = JsonSerializer.Deserialize<DiscoveryPacket>(datagram, Json);
            if (packet is null || string.IsNullOrWhiteSpace(packet.Alias) ||
                string.IsNullOrWhiteSpace(packet.Fingerprint) ||
                string.Equals(packet.Fingerprint, localFingerprint, StringComparison.OrdinalIgnoreCase) ||
                !Version.TryParse(packet.Version, out var version) || version.Major != 2 ||
                packet.Port is < 1 or > 65535 || packet.Protocol is not ("http" or "https"))
                return null;

            return packet;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public record UploadFile(string Id, string FileName, long? Size, string? Sha256);
public record PrepareRequest(DeviceInfo? Info, Dictionary<string, UploadFile?>? Files);
public record PrepareResponse(string SessionId, Dictionary<string, string> Files);

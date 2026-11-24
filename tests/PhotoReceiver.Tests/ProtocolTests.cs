using System.Net;
using System.Text.Json;
using Xunit;

namespace PhotoReceiver.Tests;

public class ProtocolTests
{
    private static readonly DeviceInfo Phone = new("iPhone", "iOS", "mobile", "remote");
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ParsesOfficialAnnouncement()
    {
        var packet = DiscoveryProtocol.Parse("""
            {"alias":"iPhone","version":"2.2","deviceModel":"iOS","deviceType":"mobile",
             "fingerprint":"remote","port":53317,"protocol":"https","announce":true}
            """u8, "local");

        Assert.NotNull(packet);
        Assert.True(packet.Announce);
        Assert.False(packet.Download);
        Assert.Equal("iPhone", packet.Alias);
        Assert.Equal("iOS", packet.DeviceModel);
        Assert.Equal("mobile", packet.DeviceType);
        Assert.Equal("remote", packet.Fingerprint);
        Assert.Equal(53317, packet.Port);
        Assert.Equal("https", packet.Protocol);
    }

    [Fact]
    public void ParsesFallbackWithoutRequestingAnotherReply()
    {
        var packet = DiscoveryProtocol.Parse(DiscoveryProtocol.Encode(Phone, false), "local");
        Assert.NotNull(packet);
        Assert.False(packet.Announce);
    }

    [Fact]
    public void IgnoresOwnAnnouncement()
    {
        Assert.Null(DiscoveryProtocol.Parse(DiscoveryProtocol.Encode(Phone, true), "REMOTE"));
    }

    [Fact]
    public void RejectsMalformedJson()
    {
        Assert.Null(DiscoveryProtocol.Parse("{"u8, "local"));
    }

    [Fact]
    public void RejectsInvalidAnnouncedPort()
    {
        var bytes = DiscoveryProtocol.Encode(Phone with { Port = 70000 }, true);
        Assert.Null(DiscoveryProtocol.Parse(bytes, "local"));
    }

    [Fact]
    public void PrepareReturnsOneDistinctTokenPerOriginalFileId()
    {
        using var sessions = new UploadSessions(Path.GetTempPath());
        var response = sessions.Prepare(Files(), IPAddress.Loopback);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response, WebJson));
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("sessionId").GetString()));
        var files = json.RootElement.GetProperty("files");
        Assert.Equal(2, files.EnumerateObject().Count());
        var first = files.GetProperty("ID-A").GetString();
        var second = files.GetProperty("id-b").GetString();
        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.False(string.IsNullOrWhiteSpace(second));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void PrepareCreatesFreshSessionsAndTokens()
    {
        using var sessions = new UploadSessions(Path.GetTempPath());
        var first = sessions.Prepare(Files(), IPAddress.Loopback);
        var second = sessions.Prepare(Files(), IPAddress.Loopback);

        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.NotEqual(first.Files["ID-A"], second.Files["ID-A"]);
    }

    [Fact]
    public void RejectsMissingFileSize()
    {
        var files = Files();
        files["ID-A"] = files["ID-A"]! with { Size = null };
        Assert.False(UploadSessions.HasValidMetadata(new PrepareRequest(Phone, files)));
    }

    private static Dictionary<string, UploadFile?> Files() => new()
    {
        ["ID-A"] = new("ID-A", "IMG_0001.HEIC", 15, null),
        ["id-b"] = new("id-b", "IMG_0002.JPG", 25, null)
    };
}

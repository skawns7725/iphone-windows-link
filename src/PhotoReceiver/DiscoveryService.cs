using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PhotoReceiver;

public sealed class DiscoveryService(DeviceInfo device, IHostApplicationLifetime lifetime,
    ILogger<DiscoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Peers may immediately connect back when they receive our announcement.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        try
        {
            await started.Task.WaitAsync(stoppingToken);
            await DiscoverAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (SocketException error)
        {
            logger.LogError(error, "UDP discovery stopped");
        }
    }

    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.ExclusiveAddressUse = false;
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryProtocol.Port));
        udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);

        var interfaces = new List<IPAddress>();
        foreach (var address in NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up && network.SupportsMulticast)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
            .Distinct())
        {
            try
            {
                udp.JoinMulticastGroup(DiscoveryProtocol.Multicast.Address, address);
                interfaces.Add(address);
            }
            catch (SocketException error)
            {
                logger.LogWarning(error, "Could not join multicast on {Address}", address);
            }
        }

        if (interfaces.Count == 0)
        {
            logger.LogWarning("No IPv4 interface joined LocalSend multicast");
            return;
        }

        using var client = new HttpClient(new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            // LocalSend peers use self-signed certificates.
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        }) { Timeout = TimeSpan.FromSeconds(1) };

        async Task BroadcastAsync(bool announce)
        {
            var bytes = DiscoveryProtocol.Encode(device, announce);
            foreach (var address in interfaces)
            {
                try
                {
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                        address.GetAddressBytes());
                    await udp.SendAsync(bytes, DiscoveryProtocol.Multicast, cancellationToken);
                }
                catch (SocketException error)
                {
                    logger.LogWarning(error, "Could not send multicast on {Address}", address);
                }
            }
        }

        await BroadcastAsync(true);
        while (!cancellationToken.IsCancellationRequested)
        {
            var datagram = await udp.ReceiveAsync(cancellationToken);
            var packet = DiscoveryProtocol.Parse(datagram.Buffer, device.Fingerprint);
            if (packet is not { Announce: true })
                continue;

            var uri = new UriBuilder(packet.Protocol, datagram.RemoteEndPoint.Address.ToString(),
                packet.Port, "/api/localsend/v2/register").Uri;
            try
            {
                using var response = await client.PostAsJsonAsync(uri, device, cancellationToken);
                if (response.IsSuccessStatusCode)
                    continue;
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }

            await BroadcastAsync(false);
        }
    }
}

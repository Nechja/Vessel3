using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;

namespace Vessel3.Server.Hosting;

internal static class VesselHostExtensions
{
    public static void ConfigureVesselHost(this WebApplicationBuilder builder)
    {
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 5L * 1024 * 1024 * 1024;
            options.Limits.MinRequestBodyDataRate = null;
            options.Limits.MinResponseDataRate = null;
        });
        builder.Services.Configure<SocketTransportOptions>(options =>
        {
            options.CreateBoundListenSocket = endpoint =>
            {
                var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                if (endpoint is IPEndPoint ipEndPoint && ipEndPoint.Address == IPAddress.IPv6Any)
                {
                    socket.DualMode = true;
                }
                try
                {
                    socket.Bind(endpoint);
                }
                catch (SocketException ex)
                {
                    Console.Error.WriteLine($"[VesselHost] Bind failed on {endpoint} (family={endpoint.AddressFamily}, socketFamily={socket.AddressFamily}, reuseAddr={socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress)}): {ex.SocketErrorCode} ({ex.Message})");
                    throw;
                }
                return socket;
            };
        });
    }
}


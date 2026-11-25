using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ChatLan.Core;

// Découverte des autres PC du réseau local : annonces UDP en diffusion (broadcast), sans serveur ni internet.
public sealed class Discovery
{
    public const int UdpPort = 45454;

    readonly string _peerId;
    readonly Func<string> _name;
    readonly Func<int> _port;
    CancellationTokenSource? _cts;
    UdpClient? _listener;
    UdpClient? _sender;

    public bool Running { get; private set; }

    public event Action<string, string, IPAddress, int>? PeerSeen;   // id, nom, adresse IP, port du mini-serveur
    public event Action<string>? PeerBye;                            // id

    public Discovery(string peerId, Func<string> name, Func<int> port)
    {
        _peerId = peerId; _name = name; _port = port;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        try
        {
            var listener = new UdpClient(AddressFamily.InterNetwork);
            listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Client.Bind(new IPEndPoint(IPAddress.Any, UdpPort));
            _listener = listener;
            _sender = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            Running = true;
            _ = Task.Run(() => ReceiveLoopAsync(token));
            _ = Task.Run(() => AnnounceLoopAsync(token));
        }
        catch
        {
            // Le port UDP est indisponible : la connexion manuelle par adresse reste possible
            Running = false;
        }
    }

    public void Stop()
    {
        if (!Running) return;
        try { Send(Encode("bye")); Send(Encode("bye")); } catch { }
        Running = false;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Close(); } catch { }
        try { _sender?.Close(); } catch { }
    }

    byte[] Encode(string type)
    {
        var json = JsonSerializer.Serialize(new { t = type, id = _peerId, n = _name(), p = _port(), v = 1 });
        return Encoding.UTF8.GetBytes(json);
    }

    void Send(byte[] data)
    {
        var sender = _sender;
        if (sender is null) return;
        foreach (var address in BroadcastAddresses())
        {
            try { sender.Send(data, data.Length, new IPEndPoint(address, UdpPort)); } catch { }
        }
    }

    async Task AnnounceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Send(Encode("hi")); } catch { }
            try { await Task.Delay(2500, ct); } catch { break; }
        }
    }

    async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var listener = _listener;
                if (listener is null) break;
                var result = await listener.ReceiveAsync(ct);
                Handle(result.Buffer, result.RemoteEndPoint.Address);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { await Task.Delay(300); }
        }
    }

    void Handle(byte[] data, IPAddress from)
    {
        if (data.Length == 0 || data.Length > 1024) return;
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            var type = root.GetProperty("t").GetString();
            var id = root.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(id) || id == _peerId) return;
            if (from.IsIPv4MappedToIPv6) from = from.MapToIPv4();

            if (type == "bye") { PeerBye?.Invoke(id); return; }
            if (type != "hi") return;
            var name = root.TryGetProperty("n", out var n) ? n.GetString() ?? "" : "";
            var port = root.GetProperty("p").GetInt32();
            if (port is < 1 or > 65535) return;
            PeerSeen?.Invoke(id, name, from, port);
        }
        catch { }
    }

    // Adresses de diffusion de toutes les cartes réseau actives (ex. 192.168.1.255)
    static List<IPAddress> BroadcastAddresses()
    {
        var list = new List<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    var b = new byte[4];
                    for (var i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                    var bc = new IPAddress(b);
                    if (!list.Contains(bc)) list.Add(bc);
                }
            }
        }
        catch { }
        return list;
    }

    // Adresses IPv4 de ce PC (affichées dans le profil pour le diagnostic)
    public static List<string> LocalAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork) list.Add(ua.Address.ToString());
            }
        }
        catch { }
        return list;
    }
}

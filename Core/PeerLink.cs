using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChatLan.Core;

// Connexion SORTANTE vers un autre PC (via son mini-serveur SignalR). Elle sert à lui envoyer
// des messages, des accusés de lecture, "écrit..." et à savoir s'il est en ligne.
// Chaque PC a donc une connexion sortante vers chacun de ses contacts : le réseau est symétrique.
public sealed class PeerLink
{
    readonly PeerNode _node;
    readonly CancellationTokenSource _cts = new CancellationTokenSource();
    HubConnection? _conn;
    TaskCompletionSource _nudge = NewSignal();
    volatile bool _connected;

    public string PeerId { get; }
    public IPAddress Ip { get; private set; }
    public int Port { get; private set; }
    public bool Connected => _connected;

    static TaskCompletionSource NewSignal() => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public PeerLink(PeerNode node, string peerId, IPAddress ip, int port)
    {
        _node = node; PeerId = peerId; Ip = ip; Port = port;
        _ = Task.Run(RunAsync);
    }

    public void Nudge() => _nudge.TrySetResult();

    // Vidéo : une seule image à la fois en transit par lien. Si le réseau est lent, on saute
    // des images au lieu de les empiler (sinon l'image prend du retard et l'audio aussi).
    int _videoBusy;
    public bool TryBeginVideo() => Interlocked.CompareExchange(ref _videoBusy, 1, 0) == 0;
    public void EndVideo() => Volatile.Write(ref _videoBusy, 0);

    public void UpdateEndpoint(IPAddress ip, int port)
    {
        if (Ip.Equals(ip) && Port == port) return;
        Ip = ip; Port = port;
        // Une connexion qui fonctionne n'est jamais coupée : sur un PC à plusieurs cartes réseau,
        // la découverte peut annoncer une autre adresse à chaque fois. La nouvelle adresse
        // servira si la connexion actuelle tombe.
        if (_connected) return;
        Nudge();
    }

    // Coupe la connexion en cours (la boucle se reconnecte ensuite)
    public void ForceDisconnect()
    {
        var c = _conn;
        if (c != null) _ = SafeStopAsync(c);
    }

    static async Task SafeStopAsync(HubConnection c)
    {
        try { await c.StopAsync(); } catch { }
    }

    static string HostOf(IPAddress ip) =>
        ip.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + ip + "]" : ip.ToString();

    async Task RunAsync()
    {
        var attempt = 0;
        while (!_cts.IsCancellationRequested)
        {
            var conn = new HubConnectionBuilder()
                .WithUrl($"http://{HostOf(Ip)}:{Port}/p2p")
                .Build();
            conn.KeepAliveInterval = TimeSpan.FromSeconds(5);
            conn.ServerTimeout = TimeSpan.FromSeconds(12);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            conn.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };

            var wasUp = false;
            try
            {
                using var startCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                startCts.CancelAfter(TimeSpan.FromSeconds(6));
                await conn.StartAsync(startCts.Token);
                _conn = conn;

                var remote = await conn.InvokeAsync<PeerInfoDto?>("Hello", _node.MyInfo(), startCts.Token);
                if (remote is null || remote.PeerId != PeerId) throw new InvalidOperationException("Identité inattendue.");

                _connected = true;
                wasUp = true;
                attempt = 0;
                await _node.OnLinkUpAsync(this, remote);

                await Task.WhenAny(closed.Task, Task.Delay(Timeout.Infinite, _cts.Token));
            }
            catch { }
            finally
            {
                _connected = false;
                _conn = null;
                try { await conn.DisposeAsync(); } catch { }
                if (wasUp) _node.OnLinkDown(this);
            }

            if (_cts.IsCancellationRequested) break;
            attempt++;
            var wait = TimeSpan.FromSeconds(Math.Min(30, 2 * attempt));
            _nudge = NewSignal();
            await Task.WhenAny(Task.Delay(wait, _cts.Token), _nudge.Task);
        }
    }

    // ----- appels vers le PC distant (retournent false en cas d'échec) -----
    public async Task<bool> TryCallAsync(string method, params object?[] args)
    {
        var c = _conn;
        if (!_connected || c is null) return false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await c.InvokeCoreAsync(method, args, cts.Token);
            return true;
        }
        catch { return false; }
    }

    public async Task<bool> TryCallBoolAsync(string method, params object?[] args)
    {
        var c = _conn;
        if (!_connected || c is null) return false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await c.InvokeCoreAsync<bool>(method, args, cts.Token);
        }
        catch { return false; }
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        Nudge();
        var c = _conn;
        if (c != null) await SafeStopAsync(c);
    }
}

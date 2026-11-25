using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http;

namespace ChatLan.Core;

// Brouillon de message : texte, média, réponse, réaction… (Niveau 2)
public sealed class MsgDraft
{
    public string Kind { get; init; } = Core.Kind.Text;
    public string Text { get; init; } = "";
    public string MediaId { get; init; } = "";
    public string MediaName { get; init; } = "";
    public long MediaSize { get; init; }
    public string MediaMime { get; init; } = "";
    public int DurationMs { get; init; }
    public string ReplyUid { get; init; } = "";
    public string ReplyAuthor { get; init; } = "";
    public string ReplyPreview { get; init; } = "";
    public bool Forwarded { get; init; }
}

// ===========================================================================
//  PeerNode : le "nœud" peer-to-peer de ce PC.
//   - héberge un mini-serveur (Kestrel + SignalR) pour RECEVOIR
//   - sert aussi les pièces jointes (GET /media/<id>) et la photo de profil
//   - annonce sa présence et découvre les autres (UDP)
//   - garde une connexion sortante (PeerLink) vers chaque contact pour ENVOYER
//   - conserve l'historique dans la base SQLite locale
//  Aucun serveur central : tout passe directement de PC à PC.
// ===========================================================================
public sealed partial class PeerNode
{
    public const int DefaultPort = 5050;

    readonly string _dbPath;
    readonly ProfileRow _profile;
    readonly object _profileLock = new object();
    readonly object _linkLock = new object();
    readonly Dictionary<string, PeerLink> _links = new Dictionary<string, PeerLink>();
    readonly Dictionary<string, string> _inbound = new Dictionary<string, string>();   // connectionId -> peerId
    readonly object _inboundLock = new object();
    readonly Dictionary<string, SemaphoreSlim> _flushLocks = new Dictionary<string, SemaphoreSlim>();
    readonly SemaphoreSlim _contactLock = new SemaphoreSlim(1, 1);   // évite deux insertions simultanées du même contact
    readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    readonly HttpClient _mediaHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    WebApplication? _host;
    Discovery? _discovery;

    public int Port { get; private set; }
    public bool DiscoveryRunning => _discovery?.Running ?? false;

    // ----- Événements pour l'interface (déclenchés depuis n'importe quel thread) -----
    public event Action<ContactRow>? ContactChanged;
    public event Action<MessageRow>? MessageReceived;
    public event Action<MessageRow>? MessageEdited;
    public event Action<string>? ReactionsChanged;                 // uid
    public event Action<string>? MediaReady;                       // uid
    public event Action<string[], int>? StatusChanged;             // uids, statut
    public event Action<string, bool, DateTime?>? PresenceChanged; // peerId, en ligne, dernière présence
    public event Action<string>? TypingReceived;                   // peerId
    public event Action<string>? MessageDeleted;                   // uid
    public event Action? NetworkChanged;

    public CallManager Calls { get; }

    // ----- Profil -----
    public string PeerId => _profile.PeerId;
    public string Username => _profile.Username;
    public string DisplayName { get { lock (_profileLock) return _profile.DisplayName; } }
    public string About { get { lock (_profileLock) return _profile.About; } }
    public byte[]? Avatar { get { lock (_profileLock) return _profile.Avatar; } }

    PeerNode(string dbPath, ProfileRow profile)
    {
        _dbPath = dbPath;
        _profile = profile;
        Calls = new CallManager(profile.PeerId, profile.DisplayName, PeerNameOf, SendSignal);
    }

    static void Fire(Action a) { try { a(); } catch { } }

    public AppDb Open() => new AppDb(_dbPath);

    public PeerInfoDto MyInfo()
    {
        lock (_profileLock)
            return new PeerInfoDto(_profile.PeerId, _profile.Username, _profile.DisplayName, _profile.About,
                                   _profile.Avatar != null, _profile.AvatarVersion, Port);
    }

    string? PeerNameOf(string peerId)
    {
        try { using var db = Open(); return db.Contacts.Where(c => c.PeerId == peerId).Select(c => c.DisplayName).FirstOrDefault(); }
        catch { return null; }
    }

    // Signaux d'appel : envoyés au PC distant via sa connexion (ignorés s'il est hors ligne).
    // Les signaux de contrôle sont réessayés : un paquet perdu ne doit pas laisser un appel « fantôme ».
    bool SendSignal(string peerId, string method, object?[] args)
    {
        var link = GetLink(peerId);
        if (link is not { Connected: true }) return false;
        var reliable = method is "CallInvite" or "CallAccept" or "CallDecline" or "CallEnd";
        if (reliable) _ = SendSignalRetryAsync(link, method, args);
        else if (method == "CallVideo")
        {
            if (!link.TryBeginVideo()) return true;     // lien encore occupé : on saute cette image
            _ = link.TryCallAsync(method, args).ContinueWith(_ => link.EndVideo(), TaskScheduler.Default);
        }
        else _ = link.TryCallAsync(method, args);
        return true;
    }

    static async Task SendSignalRetryAsync(PeerLink link, string method, object?[] args)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (await link.TryCallAsync(method, args)) return;
            await Task.Delay(350);
        }
    }

    // =====================================================================
    //  Démarrage / arrêt
    // =====================================================================
    public static async Task<PeerNode> StartAsync(string dbPath, ProfileRow profile)
    {
        var node = new PeerNode(dbPath, profile);
        await node.BootAsync();
        return node;
    }

    async Task BootAsync()
    {
        try { using var db = Open(); Schema.Ensure(db); } catch { }
        MediaStore.EnsureDir();

        var port = DefaultPort;
        for (var attempt = 0; ; attempt++)
        {
            try { _host = await BuildHostAsync(port); break; }
            catch when (attempt < 5) { port = FindFreePort(); }
        }
        Port = port;

        _discovery = new Discovery(PeerId, () => DisplayName, () => Port);
        _discovery.PeerSeen += OnDiscovered;
        _discovery.PeerBye += OnBye;
        _discovery.Start();

        // Contacts déjà rencontrés : on tente de les rejoindre directement
        using var db2 = Open();
        var known = await db2.Contacts.Where(c => !c.IsGroup && c.LastIp != null).ToListAsync();
        foreach (var c in known)
            if (IPAddress.TryParse(c.LastIp, out var ip))
                EnsureLink(c.PeerId, ip, c.LastPort > 0 ? c.LastPort : DefaultPort);
    }

    static int FindFreePort()
    {
        var l = new TcpListener(IPAddress.Any, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    async Task<WebApplication> BuildHostAsync(int port)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(port));
        builder.Services.AddSingleton(this);
        builder.Services.AddSignalR(o =>
        {
            o.KeepAliveInterval = TimeSpan.FromSeconds(5);
            o.ClientTimeoutInterval = TimeSpan.FromSeconds(12);
            o.MaximumReceiveMessageSize = 512 * 1024;
        });
        var app = builder.Build();
        app.MapHub<PeerHub>("/p2p");
        app.MapGet("/avatar", () =>
        {
            var a = Avatar;
            return a is null ? Results.NotFound() : Results.File(a, "image/jpeg");
        });
        // Pièces jointes : chaque PC garde sa copie, les autres la téléchargent ici
        app.MapGet("/media/{id}", (string id) =>
        {
            if (!MediaStore.IsSafeId(id) || !MediaStore.Exists(id)) return Results.NotFound();
            return Results.File(MediaStore.PathOf(id), MediaStore.MimeOf(id));
        });
        try { await app.StartAsync(); }
        catch { await app.DisposeAsync(); throw; }
        return app;
    }

    public async Task StopAsync()
    {
        ContactChanged = null; MessageReceived = null; StatusChanged = null; PresenceChanged = null;
        TypingReceived = null; MessageDeleted = null; NetworkChanged = null;
        MessageEdited = null; ReactionsChanged = null; MediaReady = null;
        StatusesChanged = null; StatusMediaReady = null;
        try { Calls.Hangup(); } catch { }
        try { Calls.Dispose(); } catch { }
        try { _discovery?.Stop(); } catch { }

        List<PeerLink> links;
        lock (_linkLock) { links = _links.Values.ToList(); _links.Clear(); }
        foreach (var l in links) { try { await l.StopAsync(); } catch { } }

        try
        {
            if (_host != null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _host.StopAsync(cts.Token);
                await _host.DisposeAsync();
            }
        }
        catch { }
    }

    // =====================================================================
    //  Liens sortants et présence
    // =====================================================================
    static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    void EnsureLink(string peerId, IPAddress ip, int port)
    {
        if (peerId == PeerId) return;
        ip = Normalize(ip);
        PeerLink link;
        lock (_linkLock)
        {
            if (!_links.TryGetValue(peerId, out link!))
            {
                link = new PeerLink(this, peerId, ip, port);
                _links[peerId] = link;
                return;
            }
        }
        link.UpdateEndpoint(ip, port);
        link.Nudge();
    }

    PeerLink? GetLink(string peerId)
    {
        lock (_linkLock) return _links.TryGetValue(peerId, out var l) ? l : null;
    }

    List<PeerLink> LinksSnapshot()
    {
        lock (_linkLock) return _links.Values.ToList();
    }

    public bool IsOnline(string peerId) => GetLink(peerId)?.Connected ?? false;
    public int OnlineCount => LinksSnapshot().Count(l => l.Connected);

    void OnDiscovered(string id, string name, IPAddress ip, int port) => EnsureLink(id, ip, port);

    void OnBye(string id) => GetLink(id)?.ForceDisconnect();

    internal async Task OnLinkUpAsync(PeerLink link, PeerInfoDto remote)
    {
        var (contact, avatarNeeded) = await UpsertContactAsync(remote, link.Ip);
        Fire(() => ContactChanged?.Invoke(contact));
        if (avatarNeeded) _ = FetchAvatarAsync(contact.PeerId);
        Fire(() => PresenceChanged?.Invoke(link.PeerId, true, null));
        Fire(() => NetworkChanged?.Invoke());
        _ = FlushAsync(link.PeerId);
        _ = SyncGroupsAsync(link.PeerId);
        _ = SyncStatusesAsync(link.PeerId);
    }

    internal void OnLinkDown(PeerLink link) => _ = LinkDownAsync(link.PeerId);

    async Task LinkDownAsync(string peerId)
    {
        var now = DateTime.UtcNow;
        try
        {
            using var db = Open();
            var c = await db.Contacts.FindAsync(peerId);
            if (c != null) { c.LastSeen = now; await db.SaveChangesAsync(); }
        }
        catch { }
        Fire(() => PresenceChanged?.Invoke(peerId, false, now));
        Fire(() => NetworkChanged?.Invoke());
    }

    // Ajoute (ou met à jour) un contact à partir des infos reçues.
    // Les deux PC peuvent se présenter en même temps : on sérialise pour éviter un doublon.
    async Task<(ContactRow, bool)> UpsertContactAsync(PeerInfoDto info, IPAddress? ip)
    {
        await _contactLock.WaitAsync();
        try { return await UpsertContactCoreAsync(info, ip); }
        finally { _contactLock.Release(); }
    }

    async Task<(ContactRow, bool)> UpsertContactCoreAsync(PeerInfoDto info, IPAddress? ip)
    {
        using var db = Open();
        var c = await db.Contacts.FindAsync(info.PeerId);
        if (c is null)
        {
            c = new ContactRow { PeerId = info.PeerId };
            db.Contacts.Add(c);
        }
        c.IsGroup = false;
        c.Username = info.Username;
        c.DisplayName = info.DisplayName;
        c.About = info.About;
        var avatarNeeded = info.HasAvatar && (c.Avatar is null || c.AvatarVersion != info.AvatarVersion);
        if (!info.HasAvatar) c.Avatar = null;
        c.HasAvatar = info.HasAvatar;
        c.AvatarVersion = info.AvatarVersion;
        if (ip != null) { c.LastIp = Normalize(ip).ToString(); c.LastPort = info.Port; }
        await db.SaveChangesAsync();
        return (c, avatarNeeded);
    }

    async Task FetchAvatarAsync(string peerId)
    {
        try
        {
            string? ip; int port;
            using (var db = Open())
            {
                var c = await db.Contacts.FindAsync(peerId);
                if (c?.LastIp is null) return;
                ip = c.LastIp; port = c.LastPort;
            }
            var bytes = await _http.GetByteArrayAsync($"http://{ip}:{port}/avatar");
            using (var db = Open())
            {
                var c = await db.Contacts.FindAsync(peerId);
                if (c is null) return;
                c.Avatar = bytes;
                await db.SaveChangesAsync();
                Fire(() => ContactChanged?.Invoke(c));
            }
        }
        catch { }
    }

    // Connexion manuelle (si la découverte automatique est bloquée par le Wi-Fi ou le pare-feu)
    public async Task<string?> ConnectManualAsync(string address)
    {
        var text = (address ?? "").Trim();
        if (text.Length == 0) return "Saisissez une adresse, par exemple 192.168.1.12:5050.";
        var host = text;
        var port = DefaultPort;
        var idx = text.LastIndexOf(':');
        if (idx > 0 && int.TryParse(text.AsSpan(idx + 1), out var p)) { host = text.Substring(0, idx); port = p; }

        IPAddress? ip;
        if (!IPAddress.TryParse(host, out ip))
        {
            try { ip = (await Dns.GetHostAddressesAsync(host)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork); }
            catch { ip = null; }
        }
        if (ip is null) return "Adresse introuvable.";

        HubConnection? conn = null;
        try
        {
            conn = new HubConnectionBuilder().WithUrl($"http://{ip}:{port}/p2p").Build();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await conn.StartAsync(cts.Token);
            var remote = await conn.InvokeAsync<PeerInfoDto?>("Hello", MyInfo(), cts.Token);
            if (remote is null) return "Réponse invalide de l'autre ordinateur.";
            if (remote.PeerId == PeerId) return "Il s'agit de votre propre ordinateur.";
            var (contact, avatarNeeded) = await UpsertContactAsync(remote, ip);
            Fire(() => ContactChanged?.Invoke(contact));
            if (avatarNeeded) _ = FetchAvatarAsync(contact.PeerId);
            EnsureLink(remote.PeerId, ip, remote.Port);
            return null;
        }
        catch { return "Connexion impossible. Vérifiez l'adresse, le réseau et le pare-feu."; }
        finally
        {
            if (conn != null) { try { await conn.DisposeAsync(); } catch { } }
        }
    }

    // =====================================================================
    //  Appels REÇUS depuis les autres PC (via PeerHub)
    // =====================================================================
    string RequirePeer(string connectionId)
    {
        lock (_inboundLock)
            if (_inbound.TryGetValue(connectionId, out var id)) return id;
        throw new HubException("Hello requis.");
    }

    internal async Task<PeerInfoDto?> OnHelloAsync(string connectionId, PeerInfoDto peer, IPAddress? ip)
    {
        if (string.IsNullOrEmpty(peer.PeerId) || peer.PeerId == PeerId) return null;
        lock (_inboundLock) _inbound[connectionId] = peer.PeerId;

        var (contact, avatarNeeded) = await UpsertContactAsync(peer, ip);
        Fire(() => ContactChanged?.Invoke(contact));
        if (avatarNeeded) _ = FetchAvatarAsync(contact.PeerId);
        // Réciprocité : on se connecte aussi vers lui pour pouvoir lui répondre
        if (ip != null && peer.Port > 0) EnsureLink(peer.PeerId, ip, peer.Port);
        return MyInfo();
    }

    internal void OnInboundClosed(string connectionId)
    {
        lock (_inboundLock) _inbound.Remove(connectionId);
    }

    static bool ValidWire(WireMessage w) =>
        !string.IsNullOrEmpty(w.Uid) && w.Uid.Length <= 64 &&
        (w.Text ?? "").Length <= Limits.MaxMessageLength;

    static MessageRow FromWire(WireMessage w, string contactId, string fromPeerId, string fromName)
    {
        var now = DateTime.UtcNow;
        var sent = DateTime.SpecifyKind(w.SentAt, DateTimeKind.Utc);
        if (sent > now.AddMinutes(5)) sent = now;
        return new MessageRow
        {
            Uid = w.Uid, ContactId = contactId, FromMe = false, Text = w.Text ?? "",
            SentAt = sent, CreatedAt = now, Status = 1, ReadLocally = false,
            Kind = string.IsNullOrEmpty(w.Kind) ? Core.Kind.Text : w.Kind,
            MediaId = w.MediaId ?? "", MediaName = w.MediaName ?? "", MediaSize = w.MediaSize,
            MediaMime = w.MediaMime ?? "", DurationMs = w.DurationMs,
            ReplyUid = w.ReplyUid ?? "", ReplyAuthor = w.ReplyAuthor ?? "", ReplyPreview = w.ReplyPreview ?? "",
            FromPeerId = fromPeerId, FromName = fromName, Forwarded = w.Forwarded
        };
    }

    internal async Task<bool> OnMessageAsync(string connectionId, WireMessage wire)
    {
        var peerId = RequirePeer(connectionId);
        if (!ValidWire(wire)) return false;

        using var db = Open();
        if (await db.Messages.AnyAsync(m => m.Uid == wire.Uid)) return true;   // doublon : déjà reçu

        var name = await db.Contacts.Where(c => c.PeerId == peerId).Select(c => c.DisplayName).FirstOrDefaultAsync();
        var row = FromWire(wire, peerId, peerId, string.IsNullOrEmpty(wire.FromName) ? name ?? "" : wire.FromName);
        db.Messages.Add(row);
        await db.SaveChangesAsync();
        Fire(() => MessageReceived?.Invoke(row));
        if (Core.Kind.IsMedia(row.Kind) && row.MediaId.Length > 0) _ = FetchMediaAsync(row, peerId);
        return true;
    }

    internal async Task<bool> OnGroupMessageAsync(string connectionId, WireMessage wire)
    {
        var peerId = RequirePeer(connectionId);
        if (!ValidWire(wire) || string.IsNullOrEmpty(wire.GroupId)) return false;

        using var db = Open();
        if (await db.Messages.AnyAsync(m => m.Uid == wire.Uid)) return true;

        var exists = await db.Contacts.FindAsync(wire.GroupId);
        if (exists is not null && !await db.GroupMembers.AnyAsync(m => m.GroupId == wire.GroupId && m.PeerId == PeerId))
            return false;   // je ne fais plus partie de ce groupe
        if (exists is null)
        {
            db.Contacts.Add(new ContactRow
            {
                PeerId = wire.GroupId, IsGroup = true, DisplayName = "Groupe",
                About = "En attente des informations du groupe"
            });
            await db.SaveChangesAsync();
            Fire(() => ContactChanged?.Invoke(new ContactRow { PeerId = wire.GroupId, IsGroup = true, DisplayName = "Groupe" }));
        }

        var name = string.IsNullOrEmpty(wire.FromName)
            ? await db.Contacts.Where(c => c.PeerId == peerId).Select(c => c.DisplayName).FirstOrDefaultAsync() ?? ""
            : wire.FromName;
        var row = FromWire(wire, wire.GroupId, string.IsNullOrEmpty(wire.FromPeerId) ? peerId : wire.FromPeerId, name);
        db.Messages.Add(row);
        await db.SaveChangesAsync();
        Fire(() => MessageReceived?.Invoke(row));
        if (Core.Kind.IsMedia(row.Kind) && row.MediaId.Length > 0)
            _ = FetchMediaAsync(row, row.FromPeerId.Length > 0 ? row.FromPeerId : peerId);
        return true;
    }

    // Rattrapage : messages manqués pendant une absence (les doublons sont ignorés)
    internal async Task OnSyncAsync(string connectionId, WireMessage[] items)
    {
        RequirePeer(connectionId);
        if (items is null || items.Length == 0) return;
        using var db = Open();
        var changes = new List<MessageRow>();
        foreach (var w in items.Take(200))
        {
            if (!ValidWire(w) || string.IsNullOrEmpty(w.GroupId)) continue;
            if (await db.Messages.AnyAsync(m => m.Uid == w.Uid)) continue;
            if (await db.Contacts.FindAsync(w.GroupId) is null)
                db.Contacts.Add(new ContactRow { PeerId = w.GroupId, IsGroup = true, DisplayName = "Groupe", About = "" });
            var row = FromWire(w, w.GroupId, string.IsNullOrEmpty(w.FromPeerId) ? "" : w.FromPeerId, w.FromName ?? "");
            db.Messages.Add(row);
            changes.Add(row);
        }
        if (changes.Count == 0) return;
        await db.SaveChangesAsync();
        foreach (var r in changes)
        {
            var row = r;
            Fire(() => MessageReceived?.Invoke(row));
            if (Core.Kind.IsMedia(row.Kind) && row.MediaId.Length > 0 && row.FromPeerId.Length > 0)
                _ = FetchMediaAsync(row, row.FromPeerId);
        }
    }

    internal async Task OnReadAsync(string connectionId, string[] uids)
    {
        RequirePeer(connectionId);
        if (uids is null || uids.Length == 0 || uids.Length > 5000) return;
        using var db = Open();
        var rows = await db.Messages
            .Where(m => m.FromMe && m.Status < 2 && uids.Contains(m.Uid))
            .ToListAsync();
        if (rows.Count == 0) return;
        foreach (var r in rows) r.Status = 2;
        await db.SaveChangesAsync();
        var changed = rows.Select(r => r.Uid).ToArray();
        Fire(() => StatusChanged?.Invoke(changed, 2));
    }

    internal Task OnTypingAsync(string connectionId)
    {
        var peerId = RequirePeer(connectionId);
        Fire(() => TypingReceived?.Invoke(peerId));
        return Task.CompletedTask;
    }

    internal async Task OnDeleteAsync(string connectionId, string uid)
    {
        var peerId = RequirePeer(connectionId);
        using var db = Open();
        var m = await db.Messages.FirstOrDefaultAsync(x => x.Uid == uid && !x.FromMe);
        if (m is null || m.Deleted) return;
        if (m.ContactId != peerId && !m.ContactId.StartsWith("g-")) return;
        m.Deleted = true; m.Text = "";
        await db.SaveChangesAsync();
        Fire(() => MessageDeleted?.Invoke(uid));
    }

    internal async Task OnEditAsync(string connectionId, string uid, string text, DateTime? editedAt)
    {
        var peerId = RequirePeer(connectionId);
        if (string.IsNullOrEmpty(uid) || text is null || text.Length > Limits.MaxMessageLength) return;
        using var db = Open();
        var m = await db.Messages.FirstOrDefaultAsync(x => x.Uid == uid && !x.FromMe);
        if (m is null || m.Deleted) return;
        if (m.ContactId != peerId && !m.ContactId.StartsWith("g-")) return;
        m.Text = text;
        m.EditedAt = editedAt?.ToUniversalTime() ?? DateTime.UtcNow;
        await db.SaveChangesAsync();
        Fire(() => MessageEdited?.Invoke(m));
    }

    internal async Task OnReactAsync(string connectionId, string uid, string emoji, bool remove)
    {
        var peerId = RequirePeer(connectionId);
        if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(emoji) || emoji.Length > 16) return;
        using var db = Open();
        var existing = await db.Reactions
            .Where(r => r.MessageUid == uid && r.AuthorPeerId == peerId && r.Emoji == emoji)
            .ToListAsync();
        if (remove)
        {
            if (existing.Count == 0) return;
            db.Reactions.RemoveRange(existing);
        }
        else
        {
            if (existing.Count > 0) return;
            db.Reactions.Add(new ReactionRow { MessageUid = uid, AuthorPeerId = peerId, Emoji = emoji });
        }
        await db.SaveChangesAsync();
        Fire(() => ReactionsChanged?.Invoke(uid));
    }

    internal async Task OnProfileAsync(string connectionId, PeerInfoDto info)
    {
        var peerId = RequirePeer(connectionId);
        if (info.PeerId != peerId) return;
        var (contact, avatarNeeded) = await UpsertContactAsync(info, null);
        Fire(() => ContactChanged?.Invoke(contact));
        if (avatarNeeded) _ = FetchAvatarAsync(contact.PeerId);
    }

    // ----- Groupes -----
    internal async Task OnGroupAsync(string connectionId, GroupDto dto)
    {
        var peerId = RequirePeer(connectionId);
        if (dto is null || string.IsNullOrEmpty(dto.GroupId) || dto.GroupId.Length > 64) return;
        if (!dto.Members.Any(m => m.PeerId == peerId)) return;   // seul un membre peut modifier le groupe
        await UpsertGroupAsync(dto, notify: true);
    }

    // ----- Appels -----
    internal void OnCallInvite(string connectionId, string callId, string fromName, bool video, CallPeerDto[] peers)
    {
        var peerId = RequirePeer(connectionId);
        Calls.HandleInvite(peerId, callId, fromName, video, peers);
    }

    internal void OnCallAccept(string connectionId, string callId) =>
        Calls.HandleAccept(RequirePeer(connectionId), callId);

    internal void OnCallDecline(string connectionId, string callId) =>
        Calls.HandleDecline(RequirePeer(connectionId), callId);

    internal void OnCallEnd(string connectionId, string callId) =>
        Calls.HandleEnd(RequirePeer(connectionId), callId);

    internal void OnCallAudio(string connectionId, string callId, byte[] data, string fromPeerId) =>
        Calls.HandleAudio(string.IsNullOrEmpty(fromPeerId) ? RequirePeer(connectionId) : fromPeerId, data);

    internal void OnCallVideo(string connectionId, string callId, byte[] data, string fromPeerId) =>
        Calls.HandleVideo(string.IsNullOrEmpty(fromPeerId) ? RequirePeer(connectionId) : fromPeerId, data);

    // =====================================================================
    //  Groupes : création et gestion
    // =====================================================================
    public async Task<ContactRow> CreateGroupAsync(string name, IEnumerable<string> memberIds)
    {
        var ids = memberIds.Distinct().Where(id => id != PeerId).Take(Limits.MaxGroupMembers - 1).ToList();
        var groupId = "g-" + Guid.NewGuid().ToString("N").Substring(0, 16);
        var members = new List<GroupMemberRow>
        {
            new() { GroupId = groupId, PeerId = PeerId, Name = DisplayName, IsAdmin = true, IsMe = true }
        };
        using (var db = Open())
        {
            foreach (var id in ids)
            {
                var c = await db.Contacts.FindAsync(id);
                if (c is null || c.IsGroup) continue;
                members.Add(new GroupMemberRow { GroupId = groupId, PeerId = id, Name = c.DisplayName, IsAdmin = false });
            }
            var contact = new ContactRow
            {
                PeerId = groupId, IsGroup = true, DisplayName = name,
                About = members.Count + " membres"
            };
            db.Contacts.Add(contact);
            db.GroupMembers.AddRange(members);
            await db.SaveChangesAsync();

            var dto = BuildGroupDto(contact, members);
            foreach (var m in members.Where(m => !m.IsMe))
                await QueueGroupAsync(db, m.PeerId, dto);
            await db.SaveChangesAsync();
        }
        var row = new ContactRow { PeerId = groupId, IsGroup = true, DisplayName = name, About = members.Count + " membres" };
        Fire(() => ContactChanged?.Invoke(row));
        foreach (var m in members.Where(m => !m.IsMe))
            if (GetLink(m.PeerId) is { Connected: true }) _ = FlushAsync(m.PeerId);
        return row;
    }

    GroupDto BuildGroupDto(ContactRow group, IEnumerable<GroupMemberRow> members) =>
        new(group.PeerId, group.DisplayName, members.Select(m => new GroupMemberDto(m.PeerId, m.Name, m.IsAdmin)).ToArray());

    async Task QueueGroupAsync(AppDb db, string peerId, GroupDto dto)
    {
        var link = GetLink(peerId);
        if (link is { Connected: true })
        {
            if (await link.TryCallAsync("GroupUpdate", (object)dto)) return;
        }
        db.Outbox.Add(new OutboxRow { ContactId = peerId, Type = "group", Payload = JsonSerializer.Serialize(dto) });
    }

    public async Task<List<GroupMemberRow>> GetGroupMembersAsync(string groupId)
    {
        using var db = Open();
        return await db.GroupMembers.Where(m => m.GroupId == groupId).OrderByDescending(m => m.IsAdmin).ToListAsync();
    }

    public async Task<string> AddGroupMembersAsync(string groupId, IEnumerable<string> peerIds)
    {
        using var db = Open();
        var group = await db.Contacts.FindAsync(groupId);
        if (group is null || !group.IsGroup) return "Groupe introuvable.";
        var me = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == groupId && m.IsMe);
        if (me is null || !me.IsAdmin) return "Seul un administrateur peut ajouter des membres.";
        var members = await db.GroupMembers.Where(m => m.GroupId == groupId).ToListAsync();
        foreach (var id in peerIds.Distinct())
        {
            if (members.Any(m => m.PeerId == id)) continue;
            if (members.Count >= Limits.MaxGroupMembers) return "Groupe complet (" + Limits.MaxGroupMembers + " membres).";
            var c = await db.Contacts.FindAsync(id);
            if (c is null || c.IsGroup) continue;
            var row = new GroupMemberRow { GroupId = groupId, PeerId = id, Name = c.DisplayName };
            db.GroupMembers.Add(row);
            members.Add(row);
        }
        group.About = members.Count + " membres";
        await db.SaveChangesAsync();
        await BroadcastGroupAsync(db, group, members);
        await db.SaveChangesAsync();
        Fire(() => ContactChanged?.Invoke(group));
        return "";
    }

    public async Task<string> RemoveGroupMemberAsync(string groupId, string peerId)
    {
        if (peerId == PeerId) return await LeaveGroupAsync(groupId);
        using var db = Open();
        var group = await db.Contacts.FindAsync(groupId);
        if (group is null || !group.IsGroup) return "Groupe introuvable.";
        var me = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == groupId && m.IsMe);
        if (me is null) return "Groupe introuvable.";
        if (!me.IsAdmin) return "Seul un administrateur peut retirer un membre.";
        var members = await db.GroupMembers.Where(m => m.GroupId == groupId).ToListAsync();
        var target = members.FirstOrDefault(m => m.PeerId == peerId);
        if (target is null) return "";
        members.Remove(target);
        db.GroupMembers.Remove(target);
        group.About = members.Count + " membres";
        await db.SaveChangesAsync();
        await BroadcastGroupAsync(db, group, members, extraPeerId: peerId);
        await db.SaveChangesAsync();
        Fire(() => ContactChanged?.Invoke(group));
        return "";
    }

    public async Task<string> SetGroupAdminAsync(string groupId, string peerId, bool admin)
    {
        using var db = Open();
        var group = await db.Contacts.FindAsync(groupId);
        if (group is null || !group.IsGroup) return "Groupe introuvable.";
        var me = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == groupId && m.IsMe);
        if (me is null || !me.IsAdmin) return "Seul un administrateur peut nommer un administrateur.";
        var members = await db.GroupMembers.Where(m => m.GroupId == groupId).ToListAsync();
        var target = members.FirstOrDefault(m => m.PeerId == peerId);
        if (target is null) return "Membre introuvable.";
        target.IsAdmin = admin;
        await db.SaveChangesAsync();
        await BroadcastGroupAsync(db, group, members);
        await db.SaveChangesAsync();
        return "";
    }

    async Task BroadcastGroupAsync(AppDb db, ContactRow group, List<GroupMemberRow> members, string? extraPeerId = null)
    {
        var dto = BuildGroupDto(group, members);
        var targets = members.Where(m => !m.IsMe).Select(m => m.PeerId).ToList();
        if (extraPeerId != null && !targets.Contains(extraPeerId)) targets.Add(extraPeerId);
        foreach (var target in targets)
            await QueueGroupAsync(db, target, dto);
    }

    async Task UpsertGroupAsync(GroupDto dto, bool notify)
    {
        using var db = Open();
        var group = await db.Contacts.FindAsync(dto.GroupId);
        if (group is null)
        {
            group = new ContactRow { PeerId = dto.GroupId, IsGroup = true };
            db.Contacts.Add(group);
        }
        group.IsGroup = true;
        group.DisplayName = dto.Name;
        group.About = dto.Members.Length + " membres";
        var existing = await db.GroupMembers.Where(m => m.GroupId == dto.GroupId).ToListAsync();
        db.GroupMembers.RemoveRange(existing);
        foreach (var m in dto.Members)
            db.GroupMembers.Add(new GroupMemberRow
            {
                GroupId = dto.GroupId, PeerId = m.PeerId, Name = m.Name,
                IsAdmin = m.IsAdmin, IsMe = m.PeerId == PeerId
            });
        if (!dto.Members.Any(m => m.PeerId == PeerId))
        {
            // Je ne suis plus membre : le groupe disparaît de mes discussions
            if (group is not null) db.Contacts.Remove(group);
        }
        await db.SaveChangesAsync();
        if (notify && group is not null) Fire(() => ContactChanged?.Invoke(group));
    }

    // =====================================================================
    //  Envoi : messages, file d'attente, accusés
    // =====================================================================
    SemaphoreSlim LockFor(string peerId)
    {
        lock (_flushLocks)
        {
            if (!_flushLocks.TryGetValue(peerId, out var s)) _flushLocks[peerId] = s = new SemaphoreSlim(1, 1);
            return s;
        }
    }

    static void Validate(MsgDraft d)
    {
        if (d.Text.Length > Limits.MaxMessageLength) throw new ArgumentException("Message trop long.");
        if (Core.Kind.IsMedia(d.Kind) && !MediaStore.IsSafeId(d.MediaId)) throw new ArgumentException("Pièce jointe invalide.");
        if (d.Kind == Core.Kind.Text && d.Text.Trim().Length == 0) throw new ArgumentException("Message vide.");
    }

    // Enregistre le message localement (statut 0). La livraison se fait ensuite avec Deliver().
    public async Task<MessageRow?> SendAsync(string contactId, MsgDraft draft)
    {
        draft = new MsgDraft
        {
            Kind = draft.Kind, Text = (draft.Text ?? "").Trim(), MediaId = draft.MediaId, MediaName = draft.MediaName,
            MediaSize = draft.MediaSize, MediaMime = draft.MediaMime, DurationMs = draft.DurationMs,
            ReplyUid = draft.ReplyUid, ReplyAuthor = draft.ReplyAuthor, ReplyPreview = draft.ReplyPreview,
            Forwarded = draft.Forwarded
        };
        Validate(draft);
        using var db = Open();
        var now = DateTime.UtcNow;
        var row = new MessageRow
        {
            Uid = Guid.NewGuid().ToString("N"), ContactId = contactId, FromMe = true, Text = draft.Text,
            SentAt = now, CreatedAt = now, Status = 0, ReadLocally = true,
            Kind = draft.Kind, MediaId = draft.MediaId, MediaName = draft.MediaName, MediaSize = draft.MediaSize,
            MediaMime = draft.MediaMime, DurationMs = draft.DurationMs,
            ReplyUid = draft.ReplyUid, ReplyAuthor = draft.ReplyAuthor, ReplyPreview = draft.ReplyPreview,
            FromPeerId = PeerId, FromName = DisplayName, Forwarded = draft.Forwarded
        };
        if (row.Kind == Core.Kind.File && row.MediaName.Length == 0) row.MediaName = row.MediaId;
        db.Messages.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    public Task<MessageRow?> SendTextAsync(string contactId, string text) =>
        SendAsync(contactId, new MsgDraft { Text = text });

    // Tente la livraison immédiate ; sinon les messages restent en file d'attente (envoyés dès le retour du contact)
    public void Deliver(string contactId)
    {
        if (contactId.StartsWith("g-")) _ = FlushGroupAsync(contactId);
        else _ = FlushAsync(contactId);
    }

    async Task<List<string>> GroupMembersOfAsync(string groupId)
    {
        using var db = Open();
        return await db.GroupMembers.Where(m => m.GroupId == groupId && !m.IsMe).Select(m => m.PeerId).ToListAsync();
    }

    WireMessage ToWire(MessageRow m, string groupId = "") => new(
        m.Uid, m.Text, m.SentAt, m.Kind, m.MediaId, m.MediaName, m.MediaSize, m.MediaMime, m.DurationMs,
        m.ReplyUid, m.ReplyAuthor, m.ReplyPreview, PeerId, DisplayName, groupId, m.Forwarded);

    async Task FlushGroupAsync(string groupId)
    {
        List<string> members;
        List<MessageRow> pending;
        using (var db = Open())
        {
            members = await db.GroupMembers.Where(m => m.GroupId == groupId && !m.IsMe).Select(m => m.PeerId).ToListAsync();
            pending = await db.Messages
                .Where(m => m.ContactId == groupId && m.FromMe && m.Status == 0 && !m.Deleted)
                .OrderBy(m => m.Id).ToListAsync();
        }
        if (pending.Count == 0) return;

        var delivered = new List<string>();
        var anySent = false;
        foreach (var m in pending)
        {
            var wire = ToWire(m, groupId);
            var sentToSomeone = false;
            foreach (var member in members)
            {
                var link = GetLink(member);
                if (link is not { Connected: true }) continue;
                if (await link.TryCallBoolAsync("ReceiveGroupMessage", wire)) { sentToSomeone = true; anySent = true; }
            }
            if (sentToSomeone) delivered.Add(m.Uid);
            else break;
        }

        if (delivered.Count > 0)
        {
            using var db = Open();
            var rows = await db.Messages.Where(m => delivered.Contains(m.Uid)).ToListAsync();
            foreach (var r in rows) r.Status = 1;
            await db.SaveChangesAsync();
            var arr = delivered.ToArray();
            Fire(() => StatusChanged?.Invoke(arr, 1));
        }
        if (anySent) _ = FlushOutboxForGroupsAsync(groupId);
    }

    async Task FlushOutboxForGroupsAsync(string groupId)
    {
        List<string> members = await GroupMembersOfAsync(groupId);
        foreach (var m in members) await FlushAsync(m);
    }

    async Task FlushAsync(string peerId)
    {
        if (peerId.StartsWith("g-")) { _ = FlushGroupAsync(peerId); return; }
        var sem = LockFor(peerId);
        await sem.WaitAsync();
        try
        {
            var link = GetLink(peerId);
            if (link is null || !link.Connected) return;

            using var db = Open();
            var delivered = new List<string>();

            var pending = await db.Messages
                .Where(m => m.ContactId == peerId && m.FromMe && m.Status == 0 && !m.Deleted)
                .OrderBy(m => m.Id).ToListAsync();
            foreach (var m in pending)
            {
                var ok = await link.TryCallBoolAsync("ReceiveMessage", ToWire(m));
                if (!ok) break;
                m.Status = 1;
                delivered.Add(m.Uid);
            }

            var items = await db.Outbox.Where(o => o.ContactId == peerId).OrderBy(o => o.Id).ToListAsync();
            foreach (var o in items)
            {
                bool ok;
                switch (o.Type)
                {
                    case "read":
                        var uids = JsonSerializer.Deserialize<string[]>(o.Payload) ?? Array.Empty<string>();
                        ok = await link.TryCallAsync("MessagesRead", (object)uids);
                        break;
                    case "delete":
                        ok = await link.TryCallAsync("DeleteForEveryone", o.Payload);
                        break;
                    case "edit":
                        var e = JsonSerializer.Deserialize<EditPayload>(o.Payload);
                        ok = e is not null && await link.TryCallAsync("EditMessage", e.Uid, e.Text, e.EditedAt);
                        break;
                    case "react":
                        var r = JsonSerializer.Deserialize<ReactPayload>(o.Payload);
                        ok = r is not null && await link.TryCallAsync("ReactMessage", r.Uid, r.Emoji, r.Remove);
                        break;
                    case "group":
                        var g = JsonSerializer.Deserialize<GroupDto>(o.Payload);
                        ok = g is not null && await link.TryCallAsync("GroupUpdate", (object)g);
                        break;
                    case "status":
                        var st = JsonSerializer.Deserialize<StatusDto>(o.Payload);
                        ok = st is not null && await link.TryCallBoolAsync("ReceiveStatus", st);
                        break;
                    case "statusdel":
                        ok = await link.TryCallAsync("StatusDeleted", o.Payload);
                        break;
                    case "statusseen":
                        ok = await link.TryCallAsync("StatusSeen", o.Payload);
                        break;
                    case "statusreact":
                        var srp = JsonSerializer.Deserialize<StatusReactPayload>(o.Payload);
                        ok = srp is not null && await link.TryCallAsync("StatusReact", srp.Id, srp.Emoji);
                        break;
                    default:
                        ok = true;
                        break;
                }
                if (!ok) break;
                db.Outbox.Remove(o);
            }

            await db.SaveChangesAsync();
            if (delivered.Count > 0)
            {
                var arr = delivered.ToArray();
                Fire(() => StatusChanged?.Invoke(arr, 1));
            }
        }
        catch { }
        finally { sem.Release(); }
    }

    // Rattrapage des messages de groupe manqués (le destinataire ignore les doublons)
    async Task SyncGroupsAsync(string peerId)
    {
        try
        {
            List<string> groups;
            using (var db = Open())
                groups = await db.GroupMembers.Where(m => m.PeerId == peerId && !m.IsMe).Select(m => m.GroupId).Distinct().ToListAsync();
            if (groups.Count == 0) return;
            var since = DateTime.UtcNow.AddDays(-Limits.GroupSyncDays);

            foreach (var g in groups)
            {
                List<WireMessage> batch;
                using (var db = Open())
                {
                    var rows = await db.Messages
                        .Where(m => m.ContactId == g && !m.Deleted && m.SentAt >= since)
                        .OrderByDescending(m => m.Id).Take(120).ToListAsync();
                    rows.Reverse();
                    batch = rows.Select(m => ToWire(m, g)).ToList();
                }
                foreach (var chunk in batch.Chunk(40))
                {
                    var link = GetLink(peerId);
                    if (link is not { Connected: true }) return;
                    if (!await link.TryCallAsync("SyncMessages", (object)chunk.ToArray())) return;
                }
            }
        }
        catch { }
    }

    sealed record EditPayload(string Uid, string Text, DateTime? EditedAt);
    sealed record ReactPayload(string Uid, string Emoji, bool Remove);

    public async Task SendTypingAsync(string contactId)
    {
        if (contactId.StartsWith("g-")) return;
        var link = GetLink(contactId);
        if (link is { Connected: true }) await link.TryCallAsync("Typing");
    }

    // Je marque comme lus les messages reçus de cette conversation et je préviens l'expéditeur
    public async Task MarkReadAsync(string contactId)
    {
        using var db = Open();
        var rows = await db.Messages
            .Where(m => m.ContactId == contactId && !m.FromMe && !m.ReadLocally && !m.Deleted)
            .ToListAsync();
        if (rows.Count == 0) return;
        foreach (var r in rows) r.ReadLocally = true;
        var uids = rows.Select(r => r.Uid).ToArray();
        await db.SaveChangesAsync();

        IEnumerable<string> targets;
        if (contactId.StartsWith("g-"))
        {
            targets = await db.GroupMembers.Where(m => m.GroupId == contactId && !m.IsMe).Select(m => m.PeerId).ToListAsync();
        }
        else targets = new[] { contactId };

        foreach (var t in targets)
        {
            var link = GetLink(t);
            if (link is { Connected: true } && await link.TryCallAsync("MessagesRead", (object)uids)) continue;
            var peer = t;
            if (!await db.Outbox.AnyAsync(o => o.ContactId == peer && o.Type == "read" && o.Payload == JsonSerializer.Serialize(uids)))
                db.Outbox.Add(new OutboxRow { ContactId = peer, Type = "read", Payload = JsonSerializer.Serialize(uids) });
        }
        await db.SaveChangesAsync();
    }

    // =====================================================================
    //  Modification, réactions
    // =====================================================================
    public async Task<string> EditMessageAsync(int id, string newText)
    {
        var text = (newText ?? "").Trim();
        if (text.Length == 0) return "empty";
        if (text.Length > Limits.MaxMessageLength) return "too_long";
        using var db = Open();
        var m = await db.Messages.FindAsync(id);
        if (m is null) return "not_found";
        if (!m.FromMe) return "forbidden";
        if (m.Deleted) return "deleted";
        if (m.Kind != Core.Kind.Text && m.Kind != Core.Kind.Image && m.Kind != Core.Kind.Video && m.Kind != Core.Kind.File)
            return "forbidden";
        var editedAt = DateTime.UtcNow;
        m.Text = text;
        m.EditedAt = editedAt;
        await db.SaveChangesAsync();

        await NotifyConversationAsync(m.ContactId, new OutboxRow
        {
            ContactId = "", Type = "edit",
            Payload = JsonSerializer.Serialize(new EditPayload(m.Uid, text, editedAt))
        }, "EditMessage", m.Uid, text, (DateTime?)editedAt);
        return "ok";
    }

    public async Task ToggleReactionAsync(int messageId, string uid, string emoji)
    {
        if (string.IsNullOrEmpty(emoji) || string.IsNullOrEmpty(uid)) return;
        bool remove;
        string contactId;
        using (var db = Open())
        {
            var message = await db.Messages.FirstOrDefaultAsync(m => m.Uid == uid);
            if (message is null) return;
            contactId = message.ContactId;
            var existing = await db.Reactions
                .Where(r => r.MessageUid == uid && r.AuthorPeerId == PeerId && r.Emoji == emoji)
                .ToListAsync();
            remove = existing.Count > 0;
            if (remove) db.Reactions.RemoveRange(existing);
            else db.Reactions.Add(new ReactionRow { MessageUid = uid, AuthorPeerId = PeerId, Emoji = emoji });
            await db.SaveChangesAsync();
        }
        Fire(() => ReactionsChanged?.Invoke(uid));

        await NotifyConversationAsync(contactId, new OutboxRow
        {
            ContactId = "", Type = "react",
            Payload = JsonSerializer.Serialize(new ReactPayload(uid, emoji, remove))
        }, "ReactMessage", uid, emoji, remove);
    }

    // Envoie une action à toute la conversation (contact seul ou tous les membres du groupe) + file d'attente
    async Task NotifyConversationAsync(string contactId, OutboxRow template, string method, params object?[] args)
    {
        IEnumerable<string> targets;
        using (var db = Open())
            targets = contactId.StartsWith("g-")
                ? await db.GroupMembers.Where(m => m.GroupId == contactId && !m.IsMe).Select(m => m.PeerId).ToListAsync()
                : new[] { contactId };

        foreach (var t in targets)
        {
            var link = GetLink(t);
            if (link is { Connected: true } && await link.TryCallAsync(method, args)) continue;
            using var db = Open();
            db.Outbox.Add(new OutboxRow { ContactId = t, Type = template.Type, Payload = template.Payload });
            await db.SaveChangesAsync();
        }
    }

    public async Task<Dictionary<string, List<ReactionRow>>> GetReactionsAsync(IEnumerable<string> uids)
    {
        var list = uids.ToList();
        var result = new Dictionary<string, List<ReactionRow>>();
        if (list.Count == 0) return result;
        using var db = Open();
        var rows = await db.Reactions.Where(r => list.Contains(r.MessageUid)).ToListAsync();
        foreach (var r in rows)
        {
            if (!result.TryGetValue(r.MessageUid, out var bucket)) result[r.MessageUid] = bucket = new List<ReactionRow>();
            bucket.Add(r);
        }
        return result;
    }

    // =====================================================================
    //  Pièces jointes
    // =====================================================================
    async Task FetchMediaAsync(MessageRow row, string fromPeerId)
    {
        if (MediaStore.Exists(row.MediaId)) { Fire(() => MediaReady?.Invoke(row.Uid)); return; }
        try
        {
            string? ip; int port;
            using (var db = Open())
            {
                var c = await db.Contacts.FindAsync(fromPeerId);
                if (c?.LastIp is null) return;
                ip = c.LastIp; port = c.LastPort;
            }
            var bytes = await _mediaHttp.GetByteArrayAsync($"http://{ip}:{port}/media/{row.MediaId}");
            if (bytes.Length == 0 || bytes.Length > Limits.MaxMediaBytes) return;
            MediaStore.SaveBytes(row.MediaId, bytes);
            Fire(() => MediaReady?.Invoke(row.Uid));
        }
        catch { }
    }

    public async Task<bool> RetryMediaAsync(int messageId)
    {
        using var db = Open();
        var row = await db.Messages.FindAsync(messageId);
        if (row is null || row.MediaId.Length == 0) return false;
        var from = row.FromMe ? "" : row.FromPeerId;
        if (from.Length == 0) return false;
        await FetchMediaAsync(row, from);
        return MediaStore.Exists(row.MediaId);
    }

    // =====================================================================
    //  Suppression
    // =====================================================================
    public async Task<bool> DeleteForMeAsync(int id)
    {
        using var db = Open();
        var m = await db.Messages.FindAsync(id);
        if (m is null) return false;
        db.Messages.Remove(m);
        await db.SaveChangesAsync();
        return true;
    }

    // Retourne "ok", "forbidden", "too_late" ou "not_found"
    public async Task<string> DeleteForEveryoneAsync(int id)
    {
        using var db = Open();
        var m = await db.Messages.FindAsync(id);
        if (m is null) return "not_found";
        if (!m.FromMe) return "forbidden";
        if (m.Deleted) return "ok";
        if (DateTime.UtcNow - m.SentAt > TimeSpan.FromMinutes(Limits.DeleteForEveryoneMinutes)) return "too_late";

        var wasDelivered = m.Status >= 1;
        m.Deleted = true; m.Text = "";
        await db.SaveChangesAsync();

        if (wasDelivered)
        {
            var uid = m.Uid;
            await NotifyConversationAsync(m.ContactId, new OutboxRow { Type = "delete", Payload = uid }, "DeleteForEveryone", uid);
        }
        return "ok";
    }

    public async Task DeleteConversationAsync(string contactId)
    {
        using var db = Open();
        var rows = await db.Messages.Where(m => m.ContactId == contactId).ToListAsync();
        db.Messages.RemoveRange(rows);
        if (contactId.StartsWith("g-"))
        {
            var members = await db.GroupMembers.Where(m => m.GroupId == contactId).ToListAsync();
            db.GroupMembers.RemoveRange(members);
            var g = await db.Contacts.FindAsync(contactId);
            if (g is not null) db.Contacts.Remove(g);
        }
        await db.SaveChangesAsync();
    }

    public async Task<string> LeaveGroupAsync(string groupId)
    {
        using var db = Open();
        var members = await db.GroupMembers.Where(m => m.GroupId == groupId).ToListAsync();
        var me = members.FirstOrDefault(m => m.IsMe);
        if (me is null) return "Groupe introuvable.";
        members.Remove(me);
        db.GroupMembers.Remove(me);
        var group = await db.Contacts.FindAsync(groupId);
        var messages = await db.Messages.Where(m => m.ContactId == groupId).ToListAsync();
        db.Messages.RemoveRange(messages);
        if (group is not null) db.Contacts.Remove(group);
        await db.SaveChangesAsync();
        if (members.Count > 0 && group is not null)
        {
            var dto = BuildGroupDto(group, members);
            using var db2 = Open();
            foreach (var m in members)
                await QueueGroupAsync(db2, m.PeerId, dto);
            await db2.SaveChangesAsync();
        }
        return "";
    }

    // =====================================================================
    //  Lecture pour l'interface
    // =====================================================================
    public async Task<List<ConversationData>> GetConversationsAsync()
    {
        using var db = Open();
        var contacts = await db.Contacts.ToListAsync();
        var unread = await db.Messages
            .Where(m => !m.FromMe && !m.ReadLocally && !m.Deleted)
            .GroupBy(m => m.ContactId)
            .Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N);

        var result = new List<ConversationData>();
        foreach (var c in contacts)
        {
            var last = await db.Messages.Where(m => m.ContactId == c.PeerId).OrderByDescending(m => m.Id).FirstOrDefaultAsync();
            result.Add(new ConversationData(c, last, unread.TryGetValue(c.PeerId, out var n) ? n : 0));
        }
        return result;
    }

    public async Task<(List<MessageRow> Items, bool HasMore)> GetHistoryAsync(string contactId, int? beforeId)
    {
        using var db = Open();
        var q = db.Messages.Where(m => m.ContactId == contactId);
        if (beforeId is int b) q = q.Where(m => m.Id < b);
        var page = await q.OrderByDescending(m => m.Id).Take(51).ToListAsync();
        var more = page.Count > 50;
        if (more) page.RemoveAt(page.Count - 1);
        page.Reverse();
        return (page, more);
    }

    // =====================================================================
    //  Mon profil
    // =====================================================================
    public async Task<string?> UpdateProfileAsync(string displayName, string about)
    {
        var name = (displayName ?? "").Trim();
        var info = (about ?? "").Trim();
        if (name.Length < 2 || name.Length > 40) return "Le nom affiché doit contenir entre 2 et 40 caractères.";
        if (info.Length > 139) return "L'info est limitée à 139 caractères.";
        using (var db = Open())
        {
            var p = await db.Profiles.FirstAsync();
            p.DisplayName = name; p.About = info;
            await db.SaveChangesAsync();
        }
        lock (_profileLock) { _profile.DisplayName = name; _profile.About = info; }
        BroadcastProfile();
        return null;
    }

    public async Task SetAvatarAsync(byte[]? jpeg)
    {
        int version;
        using (var db = Open())
        {
            var p = await db.Profiles.FirstAsync();
            p.Avatar = jpeg; p.AvatarVersion++;
            version = p.AvatarVersion;
            await db.SaveChangesAsync();
        }
        lock (_profileLock) { _profile.Avatar = jpeg; _profile.AvatarVersion = version; }
        BroadcastProfile();
    }

    void BroadcastProfile()
    {
        var info = MyInfo();
        foreach (var l in LinksSnapshot())
            if (l.Connected) _ = l.TryCallAsync("ProfileChanged", info);
    }

    public static List<string> LocalAddresses() => Discovery.LocalAddresses();
}

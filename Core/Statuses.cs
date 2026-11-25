using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ChatLan.Core;

// Ce qu'on publie : un texte, une image ou une vidéo (+ légende, fond, audience)
public sealed class StatusDraft
{
    public string Kind { get; init; } = Core.Kind.Text;
    public string Text { get; init; } = "";
    public string MediaId { get; init; } = "";
    public string MediaName { get; init; } = "";
    public string MediaMime { get; init; } = "";
    public long MediaSize { get; init; }
    public string Background { get; init; } = "0";
    public string Audience { get; init; } = "contacts";     // contacts | only
    public IReadOnlyList<string> AudienceList { get; init; } = Array.Empty<string>();
}

// Un statut + ce qu'en sait la base locale (vu par moi, réactions, visiteurs)
public sealed class StatusInfo
{
    public StatusRow Status { get; init; } = new();
    public bool SeenByMe { get; init; }
    public int ViewCount { get; init; }
    public string MyReaction { get; init; } = "";
    public List<StatusViewRow> Viewers { get; init; } = new();
    public List<(string Emoji, int Count)> Reactions { get; init; } = new();
}

// ===========================================================================
//  Statuts / stories : publication éphémère (24 h), consultable par les contacts.
//  Même philosophie que les messages : tout passe de PC à PC, avec une file
//  d'attente (Outbox) pour les contacts hors ligne.
// ===========================================================================
public sealed partial class PeerNode
{
    public event Action? StatusesChanged;
    public event Action<string>? StatusMediaReady;   // id du statut

    static bool ValidStatusId(string? id) =>
        id is { Length: >= 10 and <= 64 } && id[0] == 's' && id.All(char.IsAsciiLetterOrDigit);

    public static string NewStatusId() => "s" + Guid.NewGuid().ToString("N");

    static StatusDto ToStatusDto(StatusRow r) => new(
        r.Id, r.AuthorPeerId, r.AuthorName, r.Kind, r.Text, r.MediaId, r.MediaName, r.MediaMime,
        r.MediaSize, r.Background, r.CreatedAt, r.ExpiresAt, r.Audience, r.AudienceList);

    // ------------------------------------------------------------------ publier
    public async Task<StatusRow> PublishStatusAsync(StatusDraft d)
    {
        var now = DateTime.UtcNow;
        var text = (d.Text ?? "").Trim();
        if (text.Length > Limits.MaxStatusText) text = text[..Limits.MaxStatusText];
        var kind = d.Kind is Core.Kind.Image or Core.Kind.Video ? d.Kind : Core.Kind.Text;
        if (kind != Core.Kind.Text && !MediaStore.IsSafeId(d.MediaId))
            throw new ArgumentException("Pièce jointe invalide.");
        if (kind == Core.Kind.Text && text.Length == 0)
            throw new ArgumentException("Le statut est vide.");

        var row = new StatusRow
        {
            Id = NewStatusId(),
            AuthorPeerId = PeerId,
            AuthorName = DisplayName,
            Kind = kind,
            Text = text,
            MediaId = d.MediaId ?? "",
            MediaName = d.MediaName ?? "",
            MediaMime = d.MediaMime ?? "",
            MediaSize = d.MediaSize,
            Background = string.IsNullOrEmpty(d.Background) ? "0" : d.Background,
            Audience = d.Audience == "only" ? "only" : "contacts",
            AudienceList = string.Join(";", (d.AudienceList ?? Array.Empty<string>()).Distinct().Where(p => p != PeerId)),
            CreatedAt = now,
            ExpiresAt = now.AddHours(Limits.StatusHours),
            FromMe = true
        };
        using (var db = Open())
        {
            db.Statuses.Add(row);
            await db.SaveChangesAsync();
        }
        Fire(() => StatusesChanged?.Invoke());

        foreach (var peer in await StatusRecipientsAsync(row))
        {
            var link = GetLink(peer);
            if (link is { Connected: true }) _ = PushStatusAsync(link, row);
            else await EnqueueAsync(peer, "status", JsonSerializer.Serialize(ToStatusDto(row)));
        }
        return row;
    }

    async Task<List<string>> StatusRecipientsAsync(StatusRow row)
    {
        if (row.Audience == "only")
            return row.AudienceList.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Distinct().Where(p => p != PeerId).ToList();
        using var db = Open();
        return await db.Contacts.Where(c => !c.IsGroup && c.PeerId != PeerId).Select(c => c.PeerId).ToListAsync();
    }

    async Task PushStatusAsync(PeerLink link, StatusRow row)
    {
        try { await link.TryCallBoolAsync("ReceiveStatus", ToStatusDto(row)); } catch { }
    }

    async Task EnqueueAsync(string peerId, string type, string payload)
    {
        try
        {
            using var db = Open();
            db.Outbox.Add(new OutboxRow { ContactId = peerId, Type = type, Payload = payload });
            await db.SaveChangesAsync();
        }
        catch { }
    }

    // ------------------------------------------------------------------ recevoir
    internal async Task<bool> OnStatusAsync(string connectionId, StatusDto dto)
    {
        var peerId = RequirePeer(connectionId);
        var stored = await StoreStatusAsync(peerId, dto);
        if (stored) Fire(() => StatusesChanged?.Invoke());
        return stored;
    }

    internal async Task OnStatusSyncAsync(string connectionId, StatusDto[] items)
    {
        var peerId = RequirePeer(connectionId);
        if (items is null || items.Length == 0) return;
        var any = false;
        foreach (var dto in items.Take(100))
            if (await StoreStatusAsync(peerId, dto)) any = true;
        if (any) Fire(() => StatusesChanged?.Invoke());
    }

    async Task<bool> StoreStatusAsync(string peerId, StatusDto dto)
    {
        if (dto is null || dto.AuthorPeerId != peerId || !ValidStatusId(dto.Id)) return false;
        var created = DateTime.SpecifyKind(dto.CreatedAt, DateTimeKind.Utc);
        var expires = DateTime.SpecifyKind(dto.ExpiresAt, DateTimeKind.Utc);
        var now = DateTime.UtcNow;
        if (expires <= now) return false;
        if (created > now.AddMinutes(5)) created = now;
        // Statut réservé à certaines personnes : on vérifie qu'on en fait partie
        if (dto.Audience == "only" && !dto.AudienceList.Split(';').Contains(PeerId)) return false;
        var kind = dto.Kind is Core.Kind.Image or Core.Kind.Video ? dto.Kind : Core.Kind.Text;
        if (kind != Core.Kind.Text && !MediaStore.IsSafeId(dto.MediaId)) return false;
        var text = dto.Text ?? "";
        if (text.Length > Limits.MaxStatusText) return false;

        using var db = Open();
        if (await db.Statuses.AnyAsync(s => s.Id == dto.Id)) return false;
        var row = new StatusRow
        {
            Id = dto.Id,
            AuthorPeerId = peerId,
            AuthorName = dto.AuthorName ?? "",
            Kind = kind,
            Text = text,
            MediaId = dto.MediaId ?? "",
            MediaName = dto.MediaName ?? "",
            MediaMime = dto.MediaMime ?? "",
            MediaSize = dto.MediaSize,
            Background = dto.Background ?? "0",
            Audience = dto.Audience == "only" ? "only" : "contacts",
            AudienceList = dto.AudienceList ?? "",
            CreatedAt = created,
            ExpiresAt = expires,
            FromMe = false
        };
        db.Statuses.Add(row);
        await db.SaveChangesAsync();
        if (kind != Core.Kind.Text && row.MediaId.Length > 0) _ = FetchStatusMediaAsync(row);
        return true;
    }

    internal async Task OnStatusSeenAsync(string connectionId, string statusId)
    {
        var peerId = RequirePeer(connectionId);
        if (!ValidStatusId(statusId)) return;
        using var db = Open();
        var s = await db.Statuses.FindAsync(statusId);
        if (s is null || !s.FromMe) return;                     // seul l'auteur enregistre les visites
        if (await db.StatusViews.AnyAsync(v => v.StatusId == statusId && v.ViewerPeerId == peerId)) return;
        var name = await db.Contacts.Where(c => c.PeerId == peerId).Select(c => c.DisplayName).FirstOrDefaultAsync() ?? "";
        db.StatusViews.Add(new StatusViewRow
        {
            StatusId = statusId, ViewerPeerId = peerId, ViewerName = name, At = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        Fire(() => StatusesChanged?.Invoke());
    }

    internal async Task OnStatusReactAsync(string connectionId, string statusId, string emoji)
    {
        var peerId = RequirePeer(connectionId);
        if (!ValidStatusId(statusId) || string.IsNullOrEmpty(emoji) || emoji.Length > 16) return;
        using var db = Open();
        var s = await db.Statuses.FindAsync(statusId);
        if (s is null || !s.FromMe) return;
        if (await db.StatusReactions.AnyAsync(r => r.StatusId == statusId && r.AuthorPeerId == peerId && r.Emoji == emoji)) return;
        db.StatusReactions.Add(new StatusReactionRow { StatusId = statusId, AuthorPeerId = peerId, Emoji = emoji });
        await db.SaveChangesAsync();
        Fire(() => StatusesChanged?.Invoke());
    }

    internal async Task OnStatusDeletedAsync(string connectionId, string statusId)
    {
        var peerId = RequirePeer(connectionId);
        if (!ValidStatusId(statusId)) return;
        using var db = Open();
        var s = await db.Statuses.FindAsync(statusId);
        if (s is null || s.FromMe || s.AuthorPeerId != peerId) return;
        s.Deleted = true;
        await db.SaveChangesAsync();
        Fire(() => StatusesChanged?.Invoke());
    }

    // ------------------------------------------------------------------ actions locales
    // Marquer un statut comme vu : enregistré ici et signalé à l'auteur
    public async Task MarkStatusSeenAsync(string statusId)
    {
        string author;
        using (var db = Open())
        {
            var s = await db.Statuses.FindAsync(statusId);
            if (s is null || s.FromMe) return;
            if (await db.StatusViews.AnyAsync(v => v.StatusId == statusId && v.ViewerPeerId == PeerId)) return;
            db.StatusViews.Add(new StatusViewRow
            {
                StatusId = statusId, ViewerPeerId = PeerId, ViewerName = DisplayName, At = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            author = s.AuthorPeerId;
        }
        var link = GetLink(author);
        if (link is { Connected: true }) _ = link.TryCallAsync("StatusSeen", statusId);
        else await EnqueueAsync(author, "statusseen", statusId);
        Fire(() => StatusesChanged?.Invoke());
    }

    // Réagir à un statut (une seule réaction par personne, remplacée si on change)
    public async Task ReactToStatusAsync(string statusId, string emoji)
    {
        if (string.IsNullOrEmpty(emoji) || emoji.Length > 16) return;
        string author;
        using (var db = Open())
        {
            var s = await db.Statuses.FindAsync(statusId);
            if (s is null || s.FromMe) return;
            var old = await db.StatusReactions
                .Where(r => r.StatusId == statusId && r.AuthorPeerId == PeerId).ToListAsync();
            db.StatusReactions.RemoveRange(old);
            db.StatusReactions.Add(new StatusReactionRow { StatusId = statusId, AuthorPeerId = PeerId, Emoji = emoji });
            await db.SaveChangesAsync();
            author = s.AuthorPeerId;
        }
        var link = GetLink(author);
        if (link is { Connected: true }) _ = link.TryCallAsync("StatusReact", statusId, emoji);
        else await EnqueueAsync(author, "statusreact", JsonSerializer.Serialize(new StatusReactPayload(statusId, emoji)));
        Fire(() => StatusesChanged?.Invoke());
    }

    public async Task<bool> DeleteStatusAsync(string statusId)
    {
        StatusRow? row;
        using (var db = Open())
        {
            row = await db.Statuses.FindAsync(statusId);
            if (row is null || !row.FromMe) return false;
            row.Deleted = true;
            await db.SaveChangesAsync();
        }
        foreach (var peer in await StatusRecipientsAsync(row))
        {
            var link = GetLink(peer);
            if (link is { Connected: true }) _ = link.TryCallAsync("StatusDeleted", statusId);
            else await EnqueueAsync(peer, "statusdel", statusId);
        }
        Fire(() => StatusesChanged?.Invoke());
        return true;
    }

    // ------------------------------------------------------------------ lecture
    public async Task<List<StatusInfo>> GetStatusFeedAsync()
    {
        var now = DateTime.UtcNow;
        using var db = Open();
        try
        {
            // Ménage : les statuts expirés depuis plus d'un jour ne servent plus à rien
            var dead = await db.Statuses.Where(s => s.ExpiresAt < now.AddDays(-1)).ToListAsync();
            if (dead.Count > 0) { db.Statuses.RemoveRange(dead); await db.SaveChangesAsync(); }
        }
        catch { }

        var rows = await db.Statuses.Where(s => !s.Deleted && s.ExpiresAt > now)
                                    .OrderBy(s => s.CreatedAt).ToListAsync();
        var views = await db.StatusViews.ToListAsync();
        var reactions = await db.StatusReactions.ToListAsync();

        var list = new List<StatusInfo>();
        foreach (var r in rows)
        {
            var mine = views.Where(v => v.StatusId == r.Id).ToList();
            var reacts = reactions.Where(x => x.StatusId == r.Id).ToList();
            list.Add(new StatusInfo
            {
                Status = r,
                SeenByMe = mine.Any(v => v.ViewerPeerId == PeerId),
                ViewCount = mine.Count,
                Viewers = mine.OrderByDescending(v => v.At).ToList(),
                MyReaction = reacts.Where(x => x.AuthorPeerId == PeerId).Select(x => x.Emoji).FirstOrDefault() ?? "",
                Reactions = reacts.GroupBy(x => x.Emoji).Select(g => (g.Key, g.Count())).ToList()
            });
        }
        return list;
    }

    // ------------------------------------------------------------------ médias
    async Task FetchStatusMediaAsync(StatusRow row)
    {
        if (MediaStore.Exists(row.MediaId)) { Fire(() => StatusMediaReady?.Invoke(row.Id)); return; }
        try
        {
            string ip; int port;
            using (var db = Open())
            {
                var c = await db.Contacts.FindAsync(row.AuthorPeerId);
                if (c?.LastIp is null) return;
                ip = c.LastIp; port = c.LastPort;
            }
            var bytes = await _mediaHttp.GetByteArrayAsync($"http://{ip}:{port}/media/{row.MediaId}");
            if (bytes.Length == 0 || bytes.Length > Limits.MaxMediaBytes) return;
            MediaStore.SaveBytes(row.MediaId, bytes);
            Fire(() => StatusMediaReady?.Invoke(row.Id));
        }
        catch { }
    }

    public async Task<bool> RetryStatusMediaAsync(string statusId)
    {
        using var db = Open();
        var s = await db.Statuses.FindAsync(statusId);
        if (s is null || s.MediaId.Length == 0) return false;
        await FetchStatusMediaAsync(s);
        return MediaStore.Exists(s.MediaId);
    }

    // ------------------------------------------------------------------ rattrapage
    // À la connexion, on envoie nos statuts encore valides au contact qui arrive
    internal async Task SyncStatusesAsync(string peerId)
    {
        try
        {
            var now = DateTime.UtcNow;
            List<StatusRow> mine;
            using (var db = Open())
                mine = await db.Statuses.Where(s => s.FromMe && !s.Deleted && s.ExpiresAt > now)
                                        .OrderBy(s => s.CreatedAt).Take(60).ToListAsync();
            var visible = mine
                .Where(s => s.Audience != "only" || s.AudienceList.Split(';').Contains(peerId))
                .Select(ToStatusDto).ToArray();
            if (visible.Length == 0) return;
            var link = GetLink(peerId);
            if (link is not { Connected: true }) return;
            await link.TryCallAsync("SyncStatuses", (object)visible);
        }
        catch { }
    }
}

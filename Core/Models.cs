//  Tables de la base SQLite locale

public class ProfileRow
{
    public int Id { get; set; } = 1;
    public string PeerId { get; set; } = "";          // identifiant unique de cet utilisateur sur le réseau
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string About { get; set; } = "Disponible";
    public string PasswordHash { get; set; } = "";
    public byte[]? Avatar { get; set; }
    public int AvatarVersion { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Un contact OU un groupe (IsGroup)
public class ContactRow
{
    public string PeerId { get; set; } = "";
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string About { get; set; } = "";
    public bool HasAvatar { get; set; }
    public int AvatarVersion { get; set; }
    public byte[]? Avatar { get; set; }
    public DateTime? LastSeen { get; set; }
    public string? LastIp { get; set; }
    public int LastPort { get; set; }
    public bool IsGroup { get; set; }
}


public class MessageRow
{
    public int Id { get; set; }
    public string Uid { get; set; } = "";             // identifiant global du message
    public string ContactId { get; set; } = "";       // contact ou groupe de la conversation
    public bool FromMe { get; set; }
    public string Text { get; set; } = "";            // texte (légende d'un média)
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int Status { get; set; }                   // 0 = en attente / envoyé, 1 = reçu, 2 = lu
    public bool Deleted { get; set; }                 // supprimé pour tous
    public bool ReadLocally { get; set; }             // message reçu et lu par moi

    //------------------------------------------
    public string Kind { get; set; } = "text";        // text | image | video | voice | audio | file | sticker
    public string MediaId { get; set; } = "";         // nom du fichier dans le dossier média local
    public string MediaName { get; set; } = "";       // nom d'origine (document)
    public long MediaSize { get; set; }
    public string MediaMime { get; set; } = "";
    public int DurationMs { get; set; }
    public string ReplyUid { get; set; } = "";
    public string ReplyAuthor { get; set; } = "";
    public string ReplyPreview { get; set; } = "";
    public DateTime? EditedAt { get; set; }
    public string FromPeerId { get; set; } = "";      // expéditeur réel (utile dans un groupe)
    public string FromName { get; set; } = "";        // nom de l'expéditeur affiché dans un groupe
    public bool Forwarded { get; set; }               // message transféré
}

// Actions à transmettre dès que le destinataire est de nouveau joignable

public class OutboxRow
{
    public int Id { get; set; }
    public string ContactId { get; set; } = "";
    public string Type { get; set; } = "";            // read | delete | edit | react | group | callend
    public string Payload { get; set; } = "";
}

// Membres d'un groupe

public class GroupMemberRow
{
    public int Id { get; set; }
    public string GroupId { get; set; } = "";
    public string PeerId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsAdmin { get; set; }
    public bool IsMe { get; set; }
}

// Réactions : un emoji par auteur et par message
public class ReactionRow
{
    public int Id { get; set; }
    public string MessageUid { get; set; } = "";
    public string AuthorPeerId { get; set; } = "";
    public string Emoji { get; set; } = "";
}
// Statut (story) : publication éphémère de 24 h, texte / image / vidéo
public class StatusRow
{
    public string Id { get; set; } = "";
    public string AuthorPeerId { get; set; } = "";
    public string AuthorName { get; set; } = "";
    public string Kind { get; set; } = "text";          // text | image | video
    public string Text { get; set; } = "";              // texte ou légende
    public string MediaId { get; set; } = "";
    public string MediaName { get; set; } = "";
    public string MediaMime { get; set; } = "";
    public long MediaSize { get; set; }
    public string Background { get; set; } = "0";       // couleur de fond d'un statut texte
    public string Audience { get; set; } = "contacts";  // contacts | only
    public string AudienceList { get; set; } = "";      // peerIds séparés par ';' quand Audience = only
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddHours(24);
    public bool Deleted { get; set; }
    public bool FromMe { get; set; }
}
//status consulté
public class StatusViewRow
{
    public int Id { get; set; }
    public string StatusId { get; set; } = "";
    public string ViewerPeerId { get; set; } = "";
    public string ViewerName { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;
}
// Réactions à un statut
public class StatusReactionRow
{
    public int Id { get; set; }
    public string StatusId { get; set; } = "";
    public string AuthorPeerId { get; set; } = "";
    public string Emoji { get; set; } = "";
}

//  Messages échangés entre les PC

public record PeerInfoDto(string PeerId, string Username, string DisplayName, string About,
                          bool HasAvatar, int AvatarVersion, int Port);

// Un message, quel que soit son type. GroupId non vide = message de groupe (ContactId = GroupId côté destinataire).
public record WireMessage(
    string Uid, string Text, DateTime SentAt,
    string Kind = "text",
    string MediaId = "", string MediaName = "", long MediaSize = 0, string MediaMime = "", int DurationMs = 0,
    string ReplyUid = "", string ReplyAuthor = "", string ReplyPreview = "",
    string FromPeerId = "", string FromName = "", string GroupId = "",
    bool Forwarded = false);

public record GroupMemberDto(string PeerId, string Name, bool IsAdmin);
public record GroupDto(string GroupId, string Name, GroupMemberDto[] Members);

// Un statut transmis entre les PC
public record StatusDto(string Id, string AuthorPeerId, string AuthorName, string Kind, string Text,
                        string MediaId, string MediaName, string MediaMime, long MediaSize, string Background,
                        DateTime CreatedAt, DateTime ExpiresAt, string Audience, string AudienceList);

public record StatusReactPayload(string Id, string Emoji);

public record CallPeerDto(string PeerId, string Name);
public record CallInviteDto(string CallId, string FromPeerId, string FromName, bool Video, CallPeerDto[] Peers);

public sealed record ConversationData(ContactRow Contact, MessageRow? Last, int Unread);

public static class Limits
{
    public const int DeleteForEveryoneMinutes = 60;
    public const int MaxMessageLength = 4000;
    public const long MaxMediaBytes = 60L * 1024 * 1024;      // 60 Mo par pièce jointe
    public const int MaxGroupMembers = 32;
    public const int GroupSyncDays = 7;
    public const int StatusHours = 24;              // durée de vie d'un statut
    public const int MaxStatusText = 700;
}

public static class Kind
{
    public const string Text = "text";
    public const string Image = "image";
    public const string Video = "video";
    public const string Voice = "voice";
    public const string Audio = "audio";
    public const string File = "file";
    public const string Sticker = "sticker";

    public static bool IsMedia(string kind) => kind != Text && kind != Sticker;
}
// Aperçu d'un message dans la liste des discussions
public static class MsgPreview
{
    public static string Of(MessageRow m)
    {
        if (m.Deleted) return m.FromMe ? "Vous avez supprimé ce message" : "Ce message a été supprimé";
        return m.Kind switch
        {
            Kind.Image => "📷 Photo" + Suffix(m),
            Kind.Video => "🎬 Vidéo" + Suffix(m),
            Kind.Voice => "🎤 Message vocal" + Suffix(m),
            Kind.Audio => "🎵 Audio" + Suffix(m),
            Kind.File => "📄 " + (m.MediaName.Length > 0 ? m.MediaName : "Document"),
            Kind.Sticker => m.Text + " Sticker",
            _ => m.Text.Replace('\n', ' ')
        };
    }

    static string Suffix(MessageRow m) => string.IsNullOrWhiteSpace(m.Text) ? "" : " · " + m.Text.Replace('\n', ' ');
}

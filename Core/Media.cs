using System.IO;
using System.Text.RegularExpressions;

namespace ChatLan.Core;

// Stockage local des pièces jointes
// Un média est envoyé par le réseau via le mini-serveur HTTP de l'expéditeur
// puis recopié chez le destinataire : chaque PC garde sa propre copie
public static class MediaStore
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChatLan", "media");

    static readonly Regex SafeId = new Regex(@"^[a-z0-9]{8,64}\.[a-z0-9]{1,8}$");

    public static bool IsSafeId(string? id) => id != null && SafeId.IsMatch(id);

    public static string PathOf(string id) => Path.Combine(Dir, id);

    public static bool Exists(string? id) => IsSafeId(id) && File.Exists(PathOf(id!));

    public static void EnsureDir() { try { Directory.CreateDirectory(Dir); } catch { } }

    public static string NewId(string extension)
    {
        var ext = (extension ?? "").Trim().TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0 || ext.Length > 8 || !Regex.IsMatch(ext, "^[a-z0-9]+$")) ext = "bin";
        return Guid.NewGuid().ToString("N") + "." + ext;
    }

    // Copie un fichier choisi par l'utilisateur dans le magasin local
public static (string Id, long Size) Import(string sourcePath)
    {
        EnsureDir();
        var info = new FileInfo(sourcePath);
        if (!info.Exists) throw new FileNotFoundException("Fichier introuvable.", sourcePath);
        if (info.Length > Limits.MaxMediaBytes) throw new InvalidOperationException("Fichier trop volumineux.");
        var id = NewId(info.Extension);
        File.Copy(sourcePath, PathOf(id), true);
        return (id, info.Length);
    }

    public static string SaveBytes(string id, byte[] bytes)
    {
        EnsureDir();
        File.WriteAllBytes(PathOf(id), bytes);
        return id;
    }

    public static byte[]? ReadBytes(string id)
    {
        try { return Exists(id) ? File.ReadAllBytes(PathOf(id)) : null; }
        catch { return null; }
    }
    public static string MimeOf(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "gif" => "image/gif",
            "bmp" => "image/bmp",
            "webp" => "image/webp",
            "mp4" or "m4v" => "video/mp4",
            "mov" => "video/quicktime",
            "avi" => "video/x-msvideo",
            "mkv" => "video/x-matroska",
            "webm" => "video/webm",
            "wmv" => "video/x-ms-wmv",
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "m4a" => "audio/mp4",
            "ogg" or "opus" => "audio/ogg",
            "aac" => "audio/aac",
            "pdf" => "application/pdf",
            "doc" => "application/msword",
            "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "xls" => "application/vnd.ms-excel",
            "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "ppt" => "application/vnd.ms-powerpoint",
            "pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            "txt" => "text/plain",
            "zip" => "application/zip",
            "rar" => "application/vnd.rar",
            _ => "application/octet-stream"
        };
    }

    // Type de message déduit de l'extension du fichier
    public static string KindOf(string fileName)
    {
        var mime = MimeOf(fileName);
        if (mime.StartsWith("image/")) return Core.Kind.Image;
        if (mime.StartsWith("video/")) return Core.Kind.Video;
        if (mime.StartsWith("audio/")) return Core.Kind.Audio;
        return Core.Kind.File;
    }

    public static bool IsGif(string? idOrName) =>
        idOrName != null && idOrName.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

    public static string SizeText(long bytes)
    {
        if (bytes <= 0) return "";
        if (bytes < 1024) return bytes + " o";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0") + " Ko";
        if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.0") + " Mo";
        return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00") + " Go";
    }

    public static string DurationText(int ms)
    {
        if (ms <= 0) return "";
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}

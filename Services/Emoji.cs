using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChatLan.Services;

// WPF n'affiche pas les emojis en couleur (polices COLR ignorées) : on utilise
// les images PNG de Twemoji (CC-BY 4.0) embarquées dans Assets/emoji.
public static class EmojiData
{
    public const string All =
        "😀😁😂🤣😊😍😘😎🤔😢😭😡👍👎👏🙏💪🎉🔥✨❤️💔😴🙄😅😉🥳😇🤗😱🤝👀✅❌🙌😋😜🤩😬🤤🥰😏🙂😌💯🎂☕🍕⚽🎵🌹🌞🌙⭐" +
        "😃😄😆😛🤪🤨🧐🤓🥺😳😰😥😓🤯😤😠🤬🥶🥵😷🤒🤕🤢🤮🤧😵💀☠️👻👽🤖💩🐱🐶🐼🦊🦁🐸🐷🐵🙈🙉🙊" +
        "🍔🍟🍩🍪🍎🍌🍓🍇🍉🍺🍷🥂🎮🎧🎸🎨🏀🏆🚗✈️🏝️🌈☀️🌧️❄️💡📚💰🔑🎯🎁🎈🧸🪄";

    public const string StickerSet = "❤️😂👍🔥🎉😮😢🙏💯🥳😎🤩🚀⭐🌹🎁";

    public const string QuickSet = "👍❤️😂😮😢🙏";

    public static string[] Split(string s)
    {
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) list.Add((string)e.Current);
        return list.ToArray();
    }
}

public static class EmojiImages
{
    static readonly Dictionary<string, ImageSource?> Cache = new();
    static readonly object Gate = new();

    public static ImageSource? Get(string? emoji)
    {
        if (string.IsNullOrEmpty(emoji)) return null;
        lock (Gate)
        {
            if (Cache.TryGetValue(emoji, out var cached)) return cached;
            ImageSource? image = null;
            try
            {
                var uri = new Uri($"pack://application:,,,/Assets/emoji/{FileName(emoji)}.png", UriKind.Absolute);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = uri;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                image = bmp;
            }
            catch { image = null; }
            Cache[emoji] = image;
            return image;
        }
    }

    // Nom de fichier Twemoji : points de code en hexadécimal, sans le sélecteur de variante
    public static string FileName(string emoji)
    {
        var parts = new List<string>();
        for (var i = 0; i < emoji.Length; i++)
        {
            var cp = char.ConvertToUtf32(emoji, i);
            if (char.IsHighSurrogate(emoji[i])) i++;
            if (cp == 0xFE0F || cp == 0xFE0E) continue;
            parts.Add(cp.ToString("x"));
        }
        return string.Join("-", parts);
    }

    // Vrai si l'emoji a une image (sinon on garde le caractère, comme pour le texte libre)
    public static bool Has(string? emoji) => Get(emoji) != null;

    public static bool IsEmojiChar(int cp) =>
        cp is >= 0x1F000 and <= 0x1FAFF ||
        cp is >= 0x2600 and <= 0x27BF ||
        cp is 0x2764 or 0x2B50 or 0x2B06 or 0x2B07 ||
        cp is 0xFE0F;

    public static bool IsEmojiElement(string element)
    {
        if (string.IsNullOrEmpty(element)) return false;
        var cp = char.ConvertToUtf32(element, 0);
        return IsEmojiChar(cp) && element != "\uFE0F";
    }
}

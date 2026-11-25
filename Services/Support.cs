using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChatLan.Services;

public class PrefsData
{
    public string LastUsername { get; set; } = "";
    public bool Dark { get; set; } = true;
}

public static class Prefs
{
    static readonly string FilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChatLan", "settings.json");

    public static PrefsData Current { get; } = Load();

    static PrefsData Load()
    {
        try { return JsonSerializer.Deserialize<PrefsData>(File.ReadAllText(FilePath)) ?? new PrefsData(); }
        catch { return new PrefsData(); }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current));
        }
        catch { }
    }
}

public static class ThemeManager
{
    public static bool IsDark => Prefs.Current.Dark;
    public static event Action? ThemeChanged;

    public static void Apply(bool dark, bool save = true)
    {
        var res = Application.Current.Resources;
        var uri = new Uri($"/ChatLan;component/Themes/Theme.{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative);
        res.MergedDictionaries[0] = new ResourceDictionary { Source = uri };
        res["Wallpaper"] = WallpaperFactory.Create(dark);
        Prefs.Current.Dark = dark;
        if (save) Prefs.Save();
        ThemeChanged?.Invoke();
    }

    public static void Toggle() => Apply(!Prefs.Current.Dark);
}

public static class Fmt
{
    static readonly CultureInfo Fr = new CultureInfo("fr-FR");

    public static DateTime Local(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc).ToLocalTime();

    public static string ListTime(DateTime l)
    {
        var t = DateTime.Today;
        if (l.Date == t) return l.ToString("HH:mm");
        if (l.Date == t.AddDays(-1)) return "Hier";
        if (l.Date > t.AddDays(-7)) return Fr.TextInfo.ToTitleCase(l.ToString("dddd", Fr));
        return l.ToString("dd/MM/yyyy");
    }

    public static string Day(DateTime l)
    {
        var t = DateTime.Today;
        if (l.Date == t) return "Aujourd'hui";
        if (l.Date == t.AddDays(-1)) return "Hier";
        return l.ToString("d MMMM yyyy", Fr);
    }

    public static string Seen(DateTime? utc)
    {
        if (utc is null) return "hors ligne";
        var l = Local(utc.Value);
        var t = DateTime.Today;
        return l.Date == t ? $"vu aujourd'hui à {l:HH:mm}"
             : l.Date == t.AddDays(-1) ? $"vu hier à {l:HH:mm}"
             : $"vu le {l:dd/MM/yyyy} à {l:HH:mm}";
    }
}

public static class Img
{
    // Réduit une image à 256 px de large et l'encode en JPEG (photo de profil)
    public static byte[] ToJpeg(string path)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.UriSource = new Uri(path);
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.DecodePixelWidth = 256;
        bi.EndInit();
        bi.Freeze();
        var enc = new JpegBitmapEncoder { QualityLevel = 85 };
        enc.Frames.Add(BitmapFrame.Create(bi));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    // Charge une image depuis un fichier local (pièce jointe), taille limitée pour la mémoire
    public static ImageSource FromFile(string path)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.UriSource = new Uri(path);
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bi.DecodePixelWidth = 900;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    public static ImageSource FromBytes(byte[] bytes)
    {
        var bi = new BitmapImage();
        using var ms = new MemoryStream(bytes);
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }
}

public static class Notifier
{
    [StructLayout(LayoutKind.Sequential)]
    struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
    [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO f);

    public static void Alert()
    {
        try
        {
            System.Media.SystemSounds.Asterisk.Play();
            var w = Application.Current.MainWindow;
            if (w is { IsActive: false })
            {
                var f = new FLASHWINFO
                {
                    cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                    hwnd = new WindowInteropHelper(w).Handle,
                    dwFlags = 0xE,
                    uCount = 3
                };
                FlashWindowEx(ref f);
            }
        }
        catch { }
    }
}

// Barre de titre Windows assortie au thème (mode sombre + couleur), sans effet sur les anciennes versions
public static class TitleBar
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    public static void Apply(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var dark = ThemeManager.IsDark ? 1 : 0;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));          // mode sombre
            if (Application.Current.TryFindResource("TitleBarColor") is SolidColorBrush bg)
            {
                var c = ColorRef(bg.Color);
                DwmSetWindowAttribute(handle, 35, ref c, sizeof(int));         // couleur de la barre (Windows 11)
            }
            if (Application.Current.TryFindResource("TitleBarText") is SolidColorBrush fg)
            {
                var c = ColorRef(fg.Color);
                DwmSetWindowAttribute(handle, 36, ref c, sizeof(int));         // couleur du texte
            }
        }
        catch { }
    }
}

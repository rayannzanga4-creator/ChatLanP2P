using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using ChatLan.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatLan.ViewModels;

// Une barre de progression par statut d'un même auteur
public partial class StatusSegment : ObservableObject
{
    [ObservableProperty] bool _done;
    [ObservableProperty] bool _active;
}

// Une ligne de la bande « Statuts » (mon statut ou les statuts d'un contact)
public partial class StatusGroup : ObservableObject
{
    public string PeerId { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsMine { get; init; }
    public ImageSource? Photo { get; init; }
    public List<StatusItem> Items { get; } = new();
    public Action<StatusGroup>? OpenRequested { get; set; }

    public int Unseen => Items.Count(i => !i.SeenByMe && !i.IsMine);
    public bool HasUnseen => Unseen > 0;
    public bool HasItems => Items.Count > 0;
    public string Label => IsMine ? "Mon statut" : Name;
    public string Subtitle => IsMine
        ? (Items.Count == 0 ? "Publier un statut" : Items.Count == 1 ? "1 publication" : $"{Items.Count} publications")
        : HasUnseen ? "Nouveau statut" : Items.Count == 0 ? "" : "Vu";

    [RelayCommand] void Open() => OpenRequested?.Invoke(this);
}

// Un statut affiché dans la visionneuse
public partial class StatusItem : ObservableObject
{
    public string Id { get; init; } = "";
    public string AuthorPeerId { get; init; } = "";
    public string AuthorName { get; init; } = "";
    public string Kind { get; init; } = Core.Kind.Text;
    public string Text { get; init; } = "";
    public string MediaId { get; init; } = "";
    public string MediaName { get; init; } = "";
    public long MediaSize { get; init; }
    public string Background { get; init; } = "0";
    public DateTime At { get; init; }
    public bool IsMine { get; init; }
    public List<StatusViewRow> Viewers { get; init; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(MediaMissing), nameof(HasPicture))] ImageSource? _photo;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MediaMissing), nameof(HasPicture))] string? _mediaPath;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ViewsText))] int _viewCount;
    [ObservableProperty] bool _seenByMe;
    [ObservableProperty] string _myReaction = "";

    public ObservableCollection<ReactionChip> Reactions { get; } = new();

    public bool IsText => Kind == Core.Kind.Text;
    public bool IsImage => Kind == Core.Kind.Image;
    public bool IsVideo => Kind == Core.Kind.Video;
    public bool HasMedia => !IsText;
    public bool HasPicture => IsImage && Photo != null;
    public bool MediaMissing => HasMedia && Photo == null && MediaPath == null;
    public string Caption => Text;
    public bool HasCaption => HasMedia && Text.Length > 0;
    public string TimeText => StatusTime.Ago(At);
    public string ViewsText => ViewCount == 0 ? "Aucune vue" : ViewCount == 1 ? "1 vue" : $"{ViewCount} vues";
    public Brush BackgroundBrush => StatusPalette.Brush(Background);
    public string HeaderName => IsMine ? "Mon statut" : AuthorName;
}

// Une pastille de couleur pour les statuts texte
public partial class StatusColorOption : ObservableObject
{
    public int Index { get; init; }
    public Brush Brush { get; init; } = Brushes.Transparent;
    [ObservableProperty] bool _isSelected;
}

// Noms affichés dans « qui a vu »
public sealed record StatusViewer(string Name, string When, string Initials);

public static class StatusTime
{
    public static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        if (d.TotalSeconds < 60) return "à l'instant";
        if (d.TotalMinutes < 60) return $"il y a {(int)d.TotalMinutes} min";
        if (d.TotalHours < 24) return $"il y a {(int)d.TotalHours} h";
        return "il y a 1 jour";
    }
}

// Fonds des statuts texte (indépendants du thème, comme WhatsApp)
public static class StatusPalette
{
    static readonly Brush[] Colors =
    {
        Freeze(Color.FromRgb(0x0F, 0x9D, 0x76)),   // émeraude
        Freeze(Color.FromRgb(0x2F, 0x6B, 0xFF)),   // bleu
        Freeze(Color.FromRgb(0x8B, 0x5C, 0xF6)),   // violet
        Freeze(Color.FromRgb(0xE0, 0x5A, 0x4B)),   // corail
        Freeze(Color.FromRgb(0x0E, 0x91, 0x9E)),   // cyan
        Freeze(Color.FromRgb(0xD1, 0x7A, 0x1F))    // ambre
    };

    static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static Brush Brush(string? index) =>
        int.TryParse(index, out var i) && i >= 0 && i < Colors.Length ? Colors[i] : Colors[0];

    public static Brush BrushAt(int index) => Brush(index.ToString());

    public static int Count => Colors.Length;
}

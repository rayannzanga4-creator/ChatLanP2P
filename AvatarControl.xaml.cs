using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ChatLan.Controls;

public partial class AvatarControl : UserControl
{
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(AvatarControl), new PropertyMetadata(48.0, OnAnyChanged));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(AvatarControl), new PropertyMetadata("", OnAnyChanged));
    public static readonly DependencyProperty PhotoProperty = DependencyProperty.Register(
        nameof(Photo), typeof(ImageSource), typeof(AvatarControl), new PropertyMetadata(null, OnAnyChanged));
    public static readonly DependencyProperty ShowOnlineProperty = DependencyProperty.Register(
        nameof(ShowOnline), typeof(bool), typeof(AvatarControl), new PropertyMetadata(false, OnAnyChanged));
    public static readonly DependencyProperty RingProperty = DependencyProperty.Register(
        nameof(Ring), typeof(Brush), typeof(AvatarControl), new PropertyMetadata(null, OnAnyChanged));

    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public ImageSource? Photo { get => (ImageSource?)GetValue(PhotoProperty); set => SetValue(PhotoProperty, value); }
    public bool ShowOnline { get => (bool)GetValue(ShowOnlineProperty); set => SetValue(ShowOnlineProperty, value); }
    public Brush? Ring { get => (Brush?)GetValue(RingProperty); set => SetValue(RingProperty, value); }

    static readonly (string, string)[] Palette =
    {
        ("#FF7A59", "#E0457B"), ("#36D1DC", "#3F7FD9"), ("#F7B733", "#F0793B"), ("#7F7FD5", "#5E5CE6"),
        ("#43CEA2", "#1E9E80"), ("#F093FB", "#C256D9"), ("#5EE7DF", "#2BA4A0"), ("#FF9A9E", "#E5677B")
    };

    public AvatarControl()
    {
        InitializeComponent();
        SetResourceReference(RingProperty, "BgElevated");
        Refresh();
    }

    static void OnAnyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((AvatarControl)d).Refresh();

    void Refresh()
    {
        if (Circle is null) return;
        Width = Size; Height = Size;

        var (c1, c2) = Palette[PickIndex(Label)];
        Circle.Fill = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(c1), (Color)ColorConverter.ConvertFromString(c2), 45);

        Initials.Text = Initialsof(Label);
        Initials.FontSize = Math.Max(11, Size * 0.38);

        var dot = Math.Max(10, Size * 0.26);
        Dot.Width = dot; Dot.Height = dot;
        Dot.Stroke = Ring;
        Dot.Visibility = ShowOnline ? Visibility.Visible : Visibility.Collapsed;

        if (Photo is { } src)
        {
            Pic.Fill = new ImageBrush(src) { Stretch = Stretch.UniformToFill };
            Pic.Visibility = Visibility.Visible;
            Initials.Visibility = Visibility.Collapsed;
        }
        else
        {
            Pic.Fill = null;
            Pic.Visibility = Visibility.Collapsed;
            Initials.Visibility = Visibility.Visible;
        }
    }

    static int PickIndex(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var h = 17;
        foreach (var ch in s) h = unchecked(h * 31 + ch);
        return Math.Abs(h % Palette.Length);
    }

    static string Initialsof(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        return (parts[0].Substring(0, 1) + parts[parts.Length - 1].Substring(0, 1)).ToUpperInvariant();
    }
}

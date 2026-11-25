using System.Windows;
using System.Windows.Controls;
using ChatLan.Services;

namespace ChatLan.Controls;

// Un emoji en couleur (image Twemoji), avec repli sur le caractère si l'image manque.
public partial class EmojiIcon : UserControl
{
    public static readonly DependencyProperty EmojiProperty = DependencyProperty.Register(
        nameof(Emoji), typeof(string), typeof(EmojiIcon), new PropertyMetadata("", OnChanged));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(EmojiIcon), new PropertyMetadata(22.0, OnChanged));

    public string Emoji { get => (string)GetValue(EmojiProperty); set => SetValue(EmojiProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public EmojiIcon()
    {
        InitializeComponent();
        Refresh();
    }

    static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((EmojiIcon)d).Refresh();

    void Refresh()
    {
        if (Pic is null) return;
        Width = Size;
        Height = Size;
        var image = EmojiImages.Get(Emoji);
        if (image != null)
        {
            Pic.Source = image;
            Pic.Visibility = Visibility.Visible;
            Fallback.Visibility = Visibility.Collapsed;
            Fallback.Text = "";
        }
        else
        {
            Pic.Source = null;
            Pic.Visibility = Visibility.Collapsed;
            Fallback.Text = Emoji ?? "";
            Fallback.FontSize = Math.Max(9, Size * 0.85);
            Fallback.Visibility = string.IsNullOrEmpty(Emoji) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}

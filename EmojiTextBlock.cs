using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ChatLan.Services;

namespace ChatLan.Controls;

// TextBlock qui remplace les emojis du texte par des images en couleur.
// (WPF n'affiche pas les polices d'emojis en couleur.)
public class EmojiTextBlock : TextBlock
{
    public static readonly DependencyProperty EmojiTextProperty = DependencyProperty.Register(
        nameof(EmojiText), typeof(string), typeof(EmojiTextBlock),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure, OnTextChanged));

    public string EmojiText
    {
        get => (string)GetValue(EmojiTextProperty);
        set => SetValue(EmojiTextProperty, value);
    }

    public EmojiTextBlock()
    {
        Loaded += (_, _) => Rebuild();
    }

    static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((EmojiTextBlock)d).Rebuild();

    void Rebuild()
    {
        Inlines.Clear();
        var text = EmojiText ?? "";
        if (text.Length == 0) return;

        var buffer = new StringBuilder();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current;
            if (EmojiImages.IsEmojiElement(element))
            {
                var image = EmojiImages.Get(element);
                if (image != null)
                {
                    Flush(buffer);
                    var size = Math.Max(12, FontSize * 1.15);
                    var pic = new Image
                    {
                        Source = image,
                        Width = size,
                        Height = size,
                        Stretch = Stretch.Uniform,
                        Margin = new Thickness(1, 0, 1, 0)
                    };
                    RenderOptions.SetBitmapScalingMode(pic, BitmapScalingMode.HighQuality);
                    Inlines.Add(new InlineUIContainer(pic) { BaselineAlignment = BaselineAlignment.Center });
                    continue;
                }
            }
            buffer.Append(element);
        }
        Flush(buffer);
    }

    void Flush(StringBuilder buffer)
    {
        if (buffer.Length == 0) return;
        Inlines.Add(new Run(buffer.ToString()));
        buffer.Clear();
    }
}

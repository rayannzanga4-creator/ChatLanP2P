using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ChatLan.Controls;

// Grand emoji animé : les « stickers / GIF » intégrés, sans fichier ni internet.
// L'animation dépend de l'emoji, donc l'expéditeur et le destinataire voient la même chose.
public partial class StickerControl : UserControl
{
    public static readonly DependencyProperty EmojiProperty = DependencyProperty.Register(
        nameof(Emoji), typeof(string), typeof(StickerControl), new PropertyMetadata("😀", OnEmojiChanged));

    public static readonly DependencyProperty EmojiSizeProperty = DependencyProperty.Register(
        nameof(EmojiSize), typeof(double), typeof(StickerControl), new PropertyMetadata(62.0, OnEmojiChanged));

    public string Emoji { get => (string)GetValue(EmojiProperty); set => SetValue(EmojiProperty, value); }
    public double EmojiSize { get => (double)GetValue(EmojiSizeProperty); set => SetValue(EmojiSizeProperty, value); }

    public StickerControl()
    {
        InitializeComponent();
        Loaded += (_, _) => Play();
    }

    static void OnEmojiChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((StickerControl)d).Play();

    void Play()
    {
        if (Glyph is null) return;
        var value = string.IsNullOrEmpty(Emoji) ? "😀" : Emoji;
        Glyph.Emoji = value;
        Glyph.Size = EmojiSize;

        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Rotate.BeginAnimation(RotateTransform.AngleProperty, null);
        Slide.BeginAnimation(TranslateTransform.YProperty, null);
        Scale.ScaleX = Scale.ScaleY = 1;
        Rotate.Angle = 0;
        Slide.Y = 0;

        if (!IsLoaded || !IsVisible) return;

        var kind = Math.Abs(value.Sum(c => c)) % 5;
        switch (kind)
        {
            case 0: Beat(1.0, 1.16, 620); break;      // battement
            case 1: Bounce(9, 560); break;            // rebond
            case 2: Spin(4200); break;                // rotation lente
            case 3: Wobble(9, 520); break;            // dandinement
            default: Beat(1.0, 1.07, 900); break;     // pulsation
        }
    }

    void Beat(double from, double to, int ms)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    void Bounce(double distance, int ms)
    {
        var anim = new DoubleAnimation(0, -distance, TimeSpan.FromMilliseconds(ms))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Slide.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    void Spin(int ms)
    {
        var anim = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(ms)) { RepeatBehavior = RepeatBehavior.Forever };
        Rotate.BeginAnimation(RotateTransform.AngleProperty, anim);
    }

    void Wobble(double angle, int ms)
    {
        var anim = new DoubleAnimation(-angle, angle, TimeSpan.FromMilliseconds(ms))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Rotate.BeginAnimation(RotateTransform.AngleProperty, anim);
    }
}

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ChatLan.Controls;

// Affiche un GIF animé (les images WPF seules n'affichent que la première image)
public partial class AnimatedGif : UserControl
{
    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(
        nameof(Path), typeof(string), typeof(AnimatedGif), new PropertyMetadata(null, OnPathChanged));

    public string? Path { get => (string?)GetValue(PathProperty); set => SetValue(PathProperty, value); }

    readonly DispatcherTimer _timer = new();
    BitmapFrame[] _frames = Array.Empty<BitmapFrame>();
    int[] _delays = Array.Empty<int>();
    int _index;

    public AnimatedGif()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => NextFrame();
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    static void OnPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((AnimatedGif)d).Reload();

    void Reload()
    {
        _timer.Stop();
        _frames = Array.Empty<BitmapFrame>();
        _delays = Array.Empty<int>();
        _index = 0;
        var path = Path;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) { Pic.Source = null; return; }

        try
        {
            var decoder = new GifBitmapDecoder(new Uri(path),
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frames = new List<BitmapFrame>();
            var delays = new List<int>();
            foreach (var frame in decoder.Frames)
            {
                BitmapSource bmp = frame;
                try
                {
                    if (frame.PixelWidth > 900)
                    {
                        var scale = 900.0 / frame.PixelWidth;
                        bmp = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale));
                    }
                    bmp.Freeze();
                }
                catch { }
                try { frames.Add(BitmapFrame.Create(bmp)); } catch { }

                var delay = 8;
                try
                {
                    if (frame.Metadata is BitmapMetadata md && md.GetQuery("/grctlext/Delay") is ushort d && d > 0)
                        delay = d * 10;
                }
                catch { }
                delays.Add(Math.Clamp(delay, 30, 500));
            }
            _frames = frames.ToArray();
            _delays = delays.ToArray();
            if (_frames.Length > 0) Pic.Source = _frames[0];
            Start();
        }
        catch
        {
            try { Pic.Source = new BitmapImage(new Uri(path)); } catch { }
        }
    }

    void Start()
    {
        if (_frames.Length <= 1 || !IsLoaded) return;
        _timer.Interval = TimeSpan.FromMilliseconds(_delays.Length > 0 ? _delays[0] : 100);
        _timer.Start();
    }

    void NextFrame()
    {
        if (_frames.Length == 0) return;
        _index = (_index + 1) % _frames.Length;
        Pic.Source = _frames[_index];
        _timer.Interval = TimeSpan.FromMilliseconds(_delays.Length > _index ? _delays[_index] : 100);
    }
}

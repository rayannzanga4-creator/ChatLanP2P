using System.Windows.Media;
using System.Windows.Threading;

namespace ChatLan.Services;

// Lecture des messages vocaux : un seul lecteur partagé, une seule lecture à la fois.
public static class VoicePlayer
{
    static readonly MediaPlayer Player = new();
    static DispatcherTimer? _timer;
    static bool _attached;

    public static string? ActiveUid { get; private set; }
    public static double Position { get; private set; }        // millisecondes
    public static double Total { get; private set; }

    public static event Action? StateChanged;

    static void Attach()
    {
        if (_attached) return;
        _attached = true;
        Player.MediaOpened += (_, _) =>
        {
            Total = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.TotalMilliseconds : 0;
            StateChanged?.Invoke();
        };
        Player.MediaEnded += (_, _) => Stop();
        Player.MediaFailed += (_, _) => Stop();
    }

    public static bool IsPlaying(string uid) => ActiveUid == uid;

    public static void Toggle(string uid, string path)
    {
        Attach();
        if (ActiveUid == uid) { Stop(); return; }
        Stop();
        try
        {
            Player.Open(new Uri(path));
            Player.Play();
            ActiveUid = uid;
            Position = 0;
            Total = 0;
            _timer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _timer.Tick -= OnTick;
            _timer.Tick += OnTick;
            _timer.Start();
            StateChanged?.Invoke();
        }
        catch { Stop(); }
    }

    public static void Pause()
    {
        Player.Pause();
        _timer?.Stop();
        StateChanged?.Invoke();
    }

    public static void Stop()
    {
        try { Player.Stop(); Player.Close(); } catch { }
        _timer?.Stop();
        ActiveUid = null;
        Position = 0;
        Total = 0;
        StateChanged?.Invoke();
    }

    static void OnTick(object? sender, EventArgs e)
    {
        Position = Player.Position.TotalMilliseconds;
        if (Total <= 0 && Player.NaturalDuration.HasTimeSpan)
            Total = Player.NaturalDuration.TimeSpan.TotalMilliseconds;
        StateChanged?.Invoke();
    }
}

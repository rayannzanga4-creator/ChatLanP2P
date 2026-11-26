using NAudio.Wave;

namespace ChatLan.Core;
// Enregistreur de messages vocaux 

public sealed class VoiceRecorder : IDisposable
{
    WaveInEvent? _mic;
    WaveFileWriter? _writer;
    string? _path;
    DateTime _started;
    readonly object _lock = new();

    public bool IsRecording { get { lock (_lock) return _mic != null; } }

    public TimeSpan Elapsed
    {
        get
        {
            lock (_lock) return _mic == null ? TimeSpan.Zero : DateTime.UtcNow - _started;
        }
    }

    public string? Start()
    {
        lock (_lock)
        {
            if (_mic != null) return null;
            try
            {
                MediaStore.EnsureDir();
                _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chatlan-voice-" + Guid.NewGuid().ToString("N") + ".wav");
                _writer = new WaveFileWriter(_path, new WaveFormat(16000, 16, 1));
                _mic = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(16000, 16, 1),
                    BufferMilliseconds = 50,
                    NumberOfBuffers = 3
                };
                _mic.DataAvailable += OnData;
                _mic.RecordingStopped += (_, _) => { };
                _mic.StartRecording();
                _started = DateTime.UtcNow;
                return null;
            }
            catch (Exception ex)
            {
                CleanupFiles();
                return "Micro indisponible : " + ex.Message;
            }
        }
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        try { lock (_lock) _writer?.Write(e.Buffer, 0, e.BytesRecorded); }
        catch { }
    }
       // Termine l'enregistrement et rend le fichier WAV prêt à envoyer

       public (string Path, int DurationMs, long Size)? Stop()
    {
        lock (_lock)
        {
            if (_mic == null || _path == null) return null;
            var duration = (int)(DateTime.UtcNow - _started).TotalMilliseconds;
            StopCapture();
            var path = _path;
            long size = 0;
            try { size = new System.IO.FileInfo(path).Length; } catch { }
            _path = null;
            return (path, Math.Max(0, duration), size);
        }
    }

    
    public void Cancel()
    {
        lock (_lock)
        {
            StopCapture();
            CleanupFiles();
            _path = null;
        }
    }

    void StopCapture()
    {
        try { if (_mic != null) { _mic.DataAvailable -= OnData; _mic.StopRecording(); } } catch { }
        try { _mic?.Dispose(); } catch { }
        _mic = null;
        try { _writer?.Flush(); _writer?.Dispose(); } catch { }
        _writer = null;
    }

    void CleanupFiles()
    {
        var p = _path;
        if (p != null) { try { System.IO.File.Delete(p); } catch { } }
    }

    public void Dispose() => Cancel();
}

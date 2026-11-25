using System.Diagnostics;
using NAudio.Wave;
using OpenCvSharp;

namespace ChatLan.Core;

public enum CallPhase { Idle, Ringing, Active }

// Un participant d'un appel (moi ou un autre PC)
public sealed class CallPeer
{
    public string PeerId { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsMe { get; init; }
    public bool Accepted { get; set; }
    public byte[]? Frame { get; set; }
    public int FrameVersion { get; set; }
}

// ===========================================================================
//  Appels audio / vidéo / de groupe, en pair-à-pair (aucun serveur).
//  - audio : micro 16 kHz mono -> SignalR -> haut-parleur (NAudio)
//  - vidéo : caméra -> JPEG 320x240 -> SignalR (OpenCV)
//  - groupe : étoile. Le lanceur diffuse vers les participants et relaie
//    ce qu'il reçoit, chaque participant n'envoie qu'au lanceur.
// ===========================================================================
public sealed class CallManager : IDisposable
{
    public const int SampleRate = 16000;
    const int FrameIntervalMs = 100;
    const double MaxVideoWidth = 640, MaxVideoHeight = 480;   // assez net pour un affichage en plein écran
    const int MaxJpegBytes = 300 * 1024;                          // limite du serveur : 512 Ko (JPEG + base64)

    readonly Func<string, string, object?[], bool> _signal;   // (peerId, méthode, arguments)
    readonly Func<string, string?> _nameOf;
    readonly object _lock = new();
    readonly List<CallPeer> _peers = new();
    readonly Dictionary<string, BufferedWaveProvider> _buffers = new();
    readonly Dictionary<string, WaveOutEvent> _outs = new();
    readonly CallPeer _me;

    WaveInEvent? _mic;
    CancellationTokenSource? _videoCts;
    string? _host;                       // pour un participant : le lanceur de l'appel

    public string MyPeerId { get; }
    public string CallId { get; private set; } = "";
    public CallPhase Phase { get; private set; } = CallPhase.Idle;
    public bool Video { get; private set; }
    public bool IsGroup { get; private set; }
    public bool IsIncoming { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public bool Muted { get; private set; }
    public bool CameraOn { get; private set; }
    public string? Note { get; private set; }      // message d'état (caméra indisponible, etc.)
    public event Action? Changed;
    public event Action<CallInviteDto>? Incoming;
    public event Action<string>? Ended;

    public CallManager(string myPeerId, string myName, Func<string, string?> nameOf,
                       Func<string, string, object?[], bool> signal)
    {
        MyPeerId = myPeerId;
        _nameOf = nameOf;
        _signal = signal;
        _me = new CallPeer { PeerId = myPeerId, Name = myName, IsMe = true, Accepted = true };
    }

    public List<CallPeer> Peers
    {
        get { lock (_lock) return _peers.Select(p => new CallPeer
        {
            PeerId = p.PeerId, Name = p.Name, IsMe = p.IsMe, Accepted = p.Accepted,
            Frame = p.Frame, FrameVersion = p.FrameVersion
        }).ToList(); }
    }

    public int AcceptedCount { get { lock (_lock) return _peers.Count(p => p.Accepted && !p.IsMe); } }
    public bool AnyActive => Phase != CallPhase.Idle;

    public TimeSpan Elapsed => StartedAt is { } s ? DateTime.UtcNow - s : TimeSpan.Zero;

    void Fire(Action a) { try { a(); } catch { } }
    void Notify() { try { Changed?.Invoke(); } catch { } }

    // =====================================================================
    //  Lancement d'un appel (1 personne ou groupe)
    // =====================================================================
    public void StartOutgoing(IEnumerable<(string PeerId, string Name)> targets, bool video)
    {
        var list = targets.Where(t => t.PeerId != MyPeerId).DistinctBy(t => t.PeerId).ToList();
        if (list.Count == 0) return;

        lock (_lock)
        {
            if (Phase != CallPhase.Idle) return;
            Reset();
            CallId = Guid.NewGuid().ToString("N");
            Video = video;
            IsGroup = list.Count > 1;
            IsIncoming = false;
            Phase = CallPhase.Ringing;
            _peers.AddRange(list.Select(t => new CallPeer { PeerId = t.PeerId, Name = t.Name }));
        }

        var dtoPeers = new List<CallPeerDto> { new(MyPeerId, _me.Name) };
        dtoPeers.AddRange(list.Select(t => new CallPeerDto(t.PeerId, t.Name)));
        foreach (var t in list)
            _signal(t.PeerId, "CallInvite", new object?[] { CallId, _me.Name, video, dtoPeers.ToArray() });

        Notify();
        _ = Task.Run(() => WatchdogAsync(CallId));
    }

    async Task WatchdogAsync(string callId)
    {
        var limit = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < limit)
        {
            await Task.Delay(500);
            lock (_lock)
            {
                if (CallId != callId || Phase == CallPhase.Idle) return;
                if (_peers.Any(p => p.Accepted && !p.IsMe)) return;   // au moins une personne a répondu
            }
        }
        lock (_lock)
        {
            if (CallId != callId || Phase != CallPhase.Ringing) return;
        }
        Hangup("Pas de réponse");
    }

    // =====================================================================
    //  Réception des signaux (appelés par le hub, sur le fil réseau)
    // =====================================================================
    public void HandleInvite(string fromPeerId, string callId, string fromName, bool video, CallPeerDto[] others)
    {
        lock (_lock)
        {
            if (Phase != CallPhase.Idle)
            {
                // Déjà en communication : on refuse poliment
                _signal(fromPeerId, "CallDecline", new object?[] { callId });
                return;
            }
            Reset();
            CallId = string.IsNullOrEmpty(callId) ? Guid.NewGuid().ToString("N") : callId;
            Video = video;
            IsIncoming = true;
            Phase = CallPhase.Ringing;
            _host = fromPeerId;
            _peers.Add(new CallPeer { PeerId = fromPeerId, Name = fromName });
            foreach (var o in others ?? Array.Empty<CallPeerDto>())
                if (o.PeerId != MyPeerId && o.PeerId != fromPeerId)
                    _peers.Add(new CallPeer { PeerId = o.PeerId, Name = o.Name });
            IsGroup = _peers.Count > 1;
        }
        Fire(() => Incoming?.Invoke(new CallInviteDto(CallId, fromPeerId, fromName, video,
            Peers.Select(p => new CallPeerDto(p.PeerId, p.Name)).ToArray())));
        Notify();
    }

    public bool AcceptIncoming()
    {
        string host;
        lock (_lock)
        {
            if (!IsIncoming || Phase != CallPhase.Ringing) return false;
            var h = _peers.FirstOrDefault(p => p.PeerId == _host);
            if (h is null) return false;
            h.Accepted = true;
            host = h.PeerId;
            Phase = CallPhase.Active;
            StartedAt = DateTime.UtcNow;
        }
        _signal(host, "CallAccept", new object?[] { CallId });
        StartMedia();
        Notify();
        return true;
    }

    public void DeclineIncoming()
    {
        string? host;
        string id;
        lock (_lock)
        {
            host = _host;
            id = CallId;
            if (Phase == CallPhase.Idle) return;
        }
        if (host != null) _signal(host, "CallDecline", new object?[] { id });
        Close("Appel refusé");
    }

    public void HandleAccept(string peerId, string callId)
    {
        var start = false;
        lock (_lock)
        {
            if (Phase == CallPhase.Idle || callId != CallId) return;
            var p = _peers.FirstOrDefault(x => x.PeerId == peerId);
            if (p is null || p.Accepted) return;
            p.Accepted = true;
            if (Phase == CallPhase.Ringing)
            {
                Phase = CallPhase.Active;
                StartedAt = DateTime.UtcNow;
                start = true;
            }
        }
        if (start) StartMedia();
        Notify();
    }

    public void HandleDecline(string peerId, string? callId)
    {
        var empty = false;
        var wasRinging = false;
        lock (_lock)
        {
            if (Phase == CallPhase.Idle) return;
            if (!string.IsNullOrEmpty(callId) && callId != CallId) return;
            var p = _peers.FirstOrDefault(x => x.PeerId == peerId);
            if (p != null) _peers.Remove(p);
            empty = !_peers.Any(p => !p.IsMe);
            wasRinging = Phase == CallPhase.Ringing;
        }
        if (empty && wasRinging) Close("Appel refusé");
        else Notify();
    }

    public void HandleEnd(string peerId, string? callId)
    {
        bool empty;
        lock (_lock)
        {
            if (Phase == CallPhase.Idle) return;
            if (!string.IsNullOrEmpty(callId) && callId != CallId) return;
            var p = _peers.FirstOrDefault(x => x.PeerId == peerId);
            if (p != null) _peers.Remove(p);
            empty = !_peers.Any(x => !x.IsMe);
        }
        if (empty) Close("Appel terminé");
        else Notify();
    }

    // =====================================================================
    //  Audio / vidéo reçus
    // =====================================================================
    public void HandleAudio(string peerId, byte[]? data)
    {
        if (data is null || data.Length == 0 || Phase != CallPhase.Active) return;
        try
        {
            var buffer = BufferFor(peerId);
            buffer.AddSamples(data, 0, data.Length);
        }
        catch { }
        Relay(peerId, "CallAudio", data);
    }

    public void HandleVideo(string peerId, byte[]? jpeg)
    {
        if (jpeg is null || jpeg.Length == 0 || Phase != CallPhase.Active) return;
        lock (_lock)
        {
            var p = _peers.FirstOrDefault(x => x.PeerId == peerId);
            if (p is null)
            {
                p = new CallPeer { PeerId = peerId, Name = _nameOf(peerId) ?? "Participant", Accepted = true };
                _peers.Add(p);
            }
            p.Frame = jpeg;
            p.FrameVersion++;
        }
        Relay(peerId, "CallVideo", jpeg);
        Notify();
    }

    // Le lanceur (et lui seul) fait suivre ce qu'il reçoit vers les autres participants
    void Relay(string fromPeerId, string method, byte[] data)
    {
        List<string> targets;
        lock (_lock)
        {
            if (_host != null) return;                 // je suis simple participant
            if (!_peers.Any(p => p.PeerId == fromPeerId)) return;
            targets = _peers.Where(p => p.Accepted && !p.IsMe && p.PeerId != fromPeerId)
                            .Select(p => p.PeerId).ToList();
        }
        foreach (var t in targets) _signal(t, method, new object?[] { CallId, data, fromPeerId });
    }

    BufferedWaveProvider BufferFor(string peerId)
    {
        BufferedWaveProvider buf;
        lock (_lock)
        {
            if (_buffers.TryGetValue(peerId, out var existing)) return existing;
            buf = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, 1))
            {
                BufferDuration = TimeSpan.FromMilliseconds(400),
                DiscardOnBufferOverflow = true,
                // IMPORTANT : true = du silence est joué quand le tampon est vide.
                // Avec false, NAudio considère le flux comme terminé au premier petit trou réseau
                // et arrête le haut-parleur pour tout le reste de l'appel (plus de voix).
                ReadFully = true
            };
            _buffers[peerId] = buf;
        }
        try
        {
            var output = new WaveOutEvent { DesiredLatency = 180, NumberOfBuffers = 3 };
            output.Init(buf);
            output.Play();
            lock (_lock) _outs[peerId] = output;
        }
        catch { }
        return buf;
    }

    // =====================================================================
    //  Micro + caméra
    // =====================================================================
    void StartMedia()
    {
        StartMic();
        if (Video) StartCamera();
        else CameraOn = false;
    }

    void StartMic()
    {
        try
        {
            if (_mic != null) return;
            _mic = new WaveInEvent
            {
                WaveFormat = new WaveFormat(SampleRate, 16, 1),
                BufferMilliseconds = 60,
                NumberOfBuffers = 3
            };
            _mic.DataAvailable += OnMicData;
            _mic.StartRecording();
        }
        catch (Exception ex)
        {
            Note = "Micro indisponible : " + ex.Message;
        }
    }

    void OnMicData(object? sender, WaveInEventArgs e)
    {
        if (Muted || Phase != CallPhase.Active) return;
        var data = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, data, 0, e.BytesRecorded);
        lock (_lock)
        {
            foreach (var p in _peers.Where(p => p.Accepted && !p.IsMe).ToList())
                _signal(p.PeerId, "CallAudio", new object?[] { CallId, data, MyPeerId });
        }
    }

    void StopMic()
    {
        var mic = _mic;
        _mic = null;
        try { if (mic != null) { mic.DataAvailable -= OnMicData; mic.StopRecording(); } } catch { }
        try { mic?.Dispose(); } catch { }
    }

    void StartCamera()
    {
        if (_videoCts != null) return;
        var cts = new CancellationTokenSource();
        _videoCts = cts;
        CameraOn = true;
        _ = Task.Run(() => CameraLoopAsync(cts.Token));
    }

    async Task CameraLoopAsync(CancellationToken ct)
    {
        VideoCapture? cap = null;
        try
        {
            cap = new VideoCapture(0, VideoCaptureAPIs.DSHOW);
            if (!cap.IsOpened()) cap = new VideoCapture(0);
            try { cap.Set(VideoCaptureProperties.FrameWidth, 1280); cap.Set(VideoCaptureProperties.FrameHeight, 720); } catch { }
            if (!cap.IsOpened())
            {
                Note = "Caméra introuvable : l'appel continue en audio.";
                CameraOn = false;
                Notify();
                return;
            }
            using var mat = new Mat();
            while (!ct.IsCancellationRequested)
            {
                if (!cap.Read(mat) || mat.Empty()) { await Task.Delay(120, ct); continue; }

                // 640x480 maximum, JPEG qualité 60 (~25 Ko) : net en plein écran, léger pour le réseau local
                var scale = Math.Min(1.0, Math.Min(MaxVideoWidth / mat.Width, MaxVideoHeight / mat.Height));
                var w = Math.Max(2, (int)(mat.Width * scale));
                var h = Math.Max(2, (int)(mat.Height * scale));
                using var small = new Mat();
                Cv2.Resize(mat, small, new OpenCvSharp.Size(w, h), interpolation: InterpolationFlags.Area);

                // Aperçu local en miroir (comme WhatsApp)
                using var mirror = new Mat();
                Cv2.Flip(small, mirror, FlipMode.Y);
                Cv2.ImEncode(".jpg", mirror, out var self, new ImageEncodingParam(ImwriteFlags.JpegQuality, 60));
                SetLocalFrame(self);

                if (CameraOn)
                {
                    Cv2.ImEncode(".jpg", small, out var jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 60));
                    if (jpeg.Length <= MaxJpegBytes)
                    lock (_lock)
                    {
                        foreach (var p in _peers.Where(p => p.Accepted && !p.IsMe).ToList())
                            _signal(p.PeerId, "CallVideo", new object?[] { CallId, jpeg, MyPeerId });
                    }
                }
                await Task.Delay(FrameIntervalMs, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Note = "Caméra indisponible : " + ex.Message;
            CameraOn = false;
            Notify();
        }
        finally
        {
            try { cap?.Release(); cap?.Dispose(); } catch { }
        }
    }

    void SetLocalFrame(byte[] jpeg)
    {
        lock (_lock)
        {
            _me.Frame = jpeg;
            _me.FrameVersion++;
        }
    }

    public void ToggleMute()
    {
        Muted = !Muted;
        Notify();
    }

    public void ToggleCamera()
    {
        if (!Video) return;
        CameraOn = !CameraOn;
        Notify();
    }

    // =====================================================================
    //  Fin d'appel
    // =====================================================================
    public void Hangup(string? reason = null)
    {
        List<string> targets;
        string id;
        lock (_lock)
        {
            if (Phase == CallPhase.Idle) return;
            id = CallId;
            targets = _peers.Where(p => !p.IsMe).Select(p => p.PeerId).ToList();
        }
        foreach (var t in targets) _signal(t, "CallEnd", new object?[] { id });
        Close(reason ?? "Appel terminé");
    }

    void Close(string reason)
    {
        StopMedia();
        lock (_lock) Reset();
        Fire(() => Ended?.Invoke(reason));
        Notify();
    }

    void Reset()
    {
        Phase = CallPhase.Idle;
        CallId = "";
        IsIncoming = false;
        IsGroup = false;
        Video = false;
        StartedAt = null;
        Muted = false;
        CameraOn = false;
        _host = null;
        Note = null;
        _peers.Clear();
        _peers.Add(new CallPeer { PeerId = _me.PeerId, Name = _me.Name, IsMe = true, Accepted = true });
    }

    void StopMedia()
    {
        StopMic();
        try { _videoCts?.Cancel(); } catch { }
        _videoCts = null;
        lock (_lock)
        {
            foreach (var o in _outs.Values) { try { o.Stop(); o.Dispose(); } catch { } }
            _outs.Clear();
            _buffers.Clear();
        }
    }

    public void Dispose()
    {
        StopMedia();
        lock (_lock) Reset();
    }
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ChatLan.Core;
using ChatLan.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatLan.ViewModels;

public enum PickKind { Image, Video, Document }

// ---------- Écran principal : contacts + conversation + profil (tout en pair-à-pair) ----------
public partial class ShellViewModel : ObservableObject
{
    readonly PeerNode _n;
    readonly Func<Task> _logout;
    readonly Dictionary<string, ConversationItem> _known = new Dictionary<string, ConversationItem>();
    readonly MessageActions _msgActions;
    readonly VoiceRecorder _recorder = new();
    int _toastTick;
    DateTime _lastTyping = DateTime.MinValue;
    ConversationItem? _toastTarget;
    MessageItem? _editingItem;
    MessageItem? _replyItem;
    MessageItem? _forwarding;
    DispatcherTimer? _recordTimer;
    DispatcherTimer? _callTimer;

    // La vue s'abonne pour faire défiler la liste des messages
    public event Action<MessageItem>? RequestScroll;
    public event Action? RequestFocusComposer;

    public ObservableCollection<ConversationItem> Conversations { get; } = new ObservableCollection<ConversationItem>();
    public ObservableCollection<MessageItem> Messages { get; } = new ObservableCollection<MessageItem>();

    // Emojis en couleur (images) et stickers animés
    public string[] Emojis { get; } = EmojiData.Split(EmojiData.All);
    public string[] Stickers { get; } = EmojiData.Split(EmojiData.StickerSet);
    public string[] QuickReactions { get; } = EmojiData.Split(EmojiData.QuickSet);

    // ----- Mon profil -----
    public string MeUsername => _n.Username;
    [ObservableProperty] string _meName = "";
    [ObservableProperty] string _meAbout = "";
    [ObservableProperty] ImageSource? _mePhoto;
    [ObservableProperty] string _editName = "";
    [ObservableProperty] string _editAbout = "";
    [ObservableProperty] bool _showProfile;
    [ObservableProperty] string _profileMessage = "";
    [ObservableProperty] bool _profileMessageIsError;
    [ObservableProperty] bool _profileBusy;
    [ObservableProperty] bool _isDark = ThemeManager.IsDark;

    // ----- Réseau pair-à-pair -----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NetworkText))] int _onlineCount;
    [ObservableProperty] string _connectAddress = "";
    [ObservableProperty] bool _connectBusy;
    public string NetworkText => OnlineCount == 0 ? "Aucun appareil en ligne" : OnlineCount == 1 ? "1 appareil en ligne" : $"{OnlineCount} appareils en ligne";
    public string MyAddresses { get; }
    public string DiscoveryText => _n.DiscoveryRunning
        ? "Découverte automatique active (UDP 45454)."
        : "Découverte automatique indisponible : utilisez la connexion manuelle par adresse.";

    // ----- Liste / navigation -----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection)), NotifyPropertyChangedFor(nameof(ShowList)), NotifyPropertyChangedFor(nameof(ShowChat))] ConversationItem? _selected;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowList)), NotifyPropertyChangedFor(nameof(ShowChat))] bool _isNarrow;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSearch))] string _search = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ListTitle))] bool _usersMode;
    [ObservableProperty] bool _filterAll = true;
    [ObservableProperty] bool _filterUnread;
    [ObservableProperty] bool _isEmpty;
    [ObservableProperty] string _emptyText = "";

    // ----- Conversation -----
    [ObservableProperty] string _draft = "";
    [ObservableProperty] bool _hasMore;
    [ObservableProperty] bool _loadingOlder;

    // ----- Réponse / modification -----
    [ObservableProperty] bool _hasReply;
    [ObservableProperty] string _replyAuthor = "";
    [ObservableProperty] string _replyPreview = "";
    [ObservableProperty] bool _isEditing;
    [ObservableProperty] string _editingPreview = "";

    // ----- Enregistrement vocal -----
    [ObservableProperty] bool _isRecording;
    [ObservableProperty] string _recordTime = "0:00";

    // ----- Groupes -----
    [ObservableProperty] bool _showNewGroup;
    [ObservableProperty] string _newGroupName = "";
    [ObservableProperty] bool _showGroupInfo;
    [ObservableProperty] string _groupTitle = "";
    [ObservableProperty] bool _iAmGroupAdmin;
    [ObservableProperty] bool _addingMembers;
    public ObservableCollection<ContactChoice> GroupPicks { get; } = new ObservableCollection<ContactChoice>();
    public ObservableCollection<GroupMemberItem> GroupMembers { get; } = new ObservableCollection<GroupMemberItem>();
    public ObservableCollection<ContactChoice> AddMemberPicks { get; } = new ObservableCollection<ContactChoice>();

    // ----- Transfert -----
    [ObservableProperty] bool _showForward;
    [ObservableProperty] string _forwardSearch = "";
    public ObservableCollection<ConversationItem> ForwardTargets { get; } = new ObservableCollection<ConversationItem>();

    // ----- Appels -----
    [ObservableProperty] bool _showCall;
    [ObservableProperty] bool _callVideo;
    [ObservableProperty] bool _callIncoming;
    [ObservableProperty] bool _callMuted;
    [ObservableProperty] bool _callCameraOn;
    [ObservableProperty] bool _callIsGroup;
    [ObservableProperty] string _callTitle = "";
    [ObservableProperty] string _callStatus = "";
    [ObservableProperty] string _callNote = "";
    public ObservableCollection<CallPeerItem> CallParticipants { get; } = new ObservableCollection<CallPeerItem>();

    // ----- Notifications -----
    [ObservableProperty] bool _toastVisible;
    [ObservableProperty] string _toastTitle = "";
    [ObservableProperty] string _toastText = "";
    [ObservableProperty] ImageSource? _toastPhoto;

    // La vue fournit les boîtes de dialogue de fichiers
    public Func<PickKind, string?>? PickFile { get; set; }

    public bool HasSelection => Selected != null;
    public bool ShowList => !IsNarrow || Selected == null;
    public bool ShowChat => !IsNarrow || Selected != null;
    public bool HasSearch => Search.Length > 0;
    public bool IsGroupChat => Selected?.IsGroup == true;
    public string ListTitle => UsersMode ? "Nouvelle discussion" : "Discussions";
    public int UnreadTotal => _known.Values.Sum(c => c.Unread);
    public bool HasUnreadTotal => UnreadTotal > 0;

    void RefreshUnread()
    {
        OnPropertyChanged(nameof(UnreadTotal));
        OnPropertyChanged(nameof(HasUnreadTotal));
    }

    public ShellViewModel(PeerNode node, Func<Task> logout)
    {
        _n = node; _logout = logout;
        _meName = node.DisplayName;
        _meAbout = node.About;
        MyAddresses = string.Join("   •   ", PeerNode.LocalAddresses().Select(a => $"{a}:{node.Port}"));
        if (MyAddresses.Length == 0) MyAddresses = $"(aucune carte réseau détectée) : {node.Port}";

        _msgActions = new MessageActions
        {
            DeleteForMe = DeleteForMeAsync,
            DeleteForAll = DeleteForEveryoneAsync,
            Reply = BeginReply,
            Edit = BeginEdit,
            Forward = BeginForward,
            React = ReactAsync,
            OpenMedia = OpenMediaFile,
            Retry = RetryMediaAsync
        };

        node.ContactChanged += c => UI(() => OnContactChanged(c));
        node.MessageReceived += m => UI(() => OnReceived(m));
        node.MessageEdited += m => UI(() => OnEdited(m));
        node.ReactionsChanged += uid => UI(() => _ = RefreshReactionsAsync(uid));
        node.MediaReady += uid => UI(() => OnMediaReady(uid));
        node.StatusChanged += (uids, st) => UI(() => OnStatus(uids, st));
        node.PresenceChanged += (id, on, seen) => UI(() => OnPresence(id, on, seen));
        node.TypingReceived += id => UI(() => { if (_known.TryGetValue(id, out var c)) c.PulseTyping(); });
        node.MessageDeleted += uid => UI(() => OnDeletedForEveryone(uid));
        node.NetworkChanged += () => UI(RefreshNetwork);
        node.StatusesChanged += () => UI(() => _ = LoadStatusesAsync());
        node.StatusMediaReady += id => UI(() => _ = RefreshStatusMediaAsync(id));

        node.Calls.Incoming += dto => UI(() => OnIncomingCall(dto));
        node.Calls.Changed += () => UI(RefreshCall);
        node.Calls.Ended += reason => UI(() => OnCallEnded(reason));

        VoicePlayer.StateChanged += () => UI(RefreshPlayback);
    }

    static void UI(Action a)
    {
        var app = Application.Current;
        if (app is null) { a(); return; }
        if (app.Dispatcher.CheckAccess()) a();
        else app.Dispatcher.Invoke(a);
    }

    public async Task InitAsync()
    {
        await ReloadAsync();
        LoadMyPhoto();
        RefreshNetwork();
        await LoadStatusesAsync();
    }

    void RefreshNetwork() => OnlineCount = _n.OnlineCount;

    // =====================================================================
    //  Contacts et conversations
    // =====================================================================
    ConversationItem Item(ContactRow c)
    {
        if (!_known.TryGetValue(c.PeerId, out var it))
        {
            it = new ConversationItem(c, !c.IsGroup && _n.IsOnline(c.PeerId))
            {
                OpenRequested = OpenItem,
                DeleteRequested = x => _ = DeleteChatAsync(x)
            };
            _known[c.PeerId] = it;
        }
        else it.Update(c);

        if (!c.IsGroup && c.HasAvatar && c.Avatar is { Length: > 0 } bytes)
        {
            if (it.Photo is null || it.PhotoVersion != c.AvatarVersion)
            {
                try { it.Photo = Img.FromBytes(bytes); it.PhotoVersion = c.AvatarVersion; } catch { }
            }
        }
        else if (!c.HasAvatar) { it.Photo = null; it.PhotoVersion = -1; }
        return it;
    }

    async Task ReloadAsync()
    {
        try
        {
            foreach (var cd in await _n.GetConversationsAsync())
            {
                var it = Item(cd.Contact);
                if (cd.Last is { } last) it.SetLast(last);
                it.Unread = cd.Unread;
            }
            Refresh();
        }
        catch { Toast("Erreur", "Impossible de charger les discussions."); }
    }

    static bool Match(ConversationItem c, string q) =>
        c.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
        c.Username.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
        c.About.Contains(q, StringComparison.CurrentCultureIgnoreCase);

    void Refresh()
    {
        var q = Search.Trim();
        List<ConversationItem> arr;
        if (UsersMode || q.Length > 0)
            arr = _known.Values
                .Where(c => !(UsersMode && c.IsGroup))
                .Where(c => q.Length == 0 || Match(c, q))
                .OrderByDescending(c => c.Online)
                .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        else
            arr = _known.Values
                .Where(c => (c.HasMessages || c.IsGroup) && (!FilterUnread || c.Unread > 0))
                .OrderByDescending(c => c.LastAt)
                .ToList();

        Conversations.Clear();
        foreach (var c in arr) Conversations.Add(c);

        IsEmpty = arr.Count == 0;
        EmptyText = UsersMode ? "Aucun contact détecté pour le moment.\nOuvrez ChatLan sur un autre PC du même réseau."
                  : HasSearch ? "Aucun résultat."
                  : FilterUnread ? "Aucun message non lu."
                  : "Aucune discussion pour le moment.\nAppuyez sur + pour en démarrer une.";
        RefreshUnread();
    }

    // =====================================================================
    //  Historique
    // =====================================================================
    MessageItem NewItem(MessageRow m) => new MessageItem(m, _msgActions, m.ContactId.StartsWith("g-"));

    void RebuildLayout()
    {
        MessageItem? prev = null;
        foreach (var m in Messages)
        {
            m.DateHeader = prev is null || prev.At.Date != m.At.Date ? Fmt.Day(m.At) : null;
            m.FirstInRun = prev is null || prev.IsMine != m.IsMine || m.HasHeader;
            prev = m;
        }
    }

    async Task LoadHistoryAsync(ConversationItem c)
    {
        try
        {
            var (items, more) = await _n.GetHistoryAsync(c.PeerId, null);
            if (Selected != c) return;
            HasMore = more;
            foreach (var m in items) AddMessage(m);
            await LoadReactionsAsync(items.Select(i => i.Uid));
            if (c.Unread > 0 || items.Any(m => !m.FromMe && !m.ReadLocally))
            {
                c.Unread = 0;
                RefreshUnread();
                await _n.MarkReadAsync(c.PeerId);
            }
        }
        catch { Toast("Erreur", "Historique indisponible."); }
    }

    async Task LoadReactionsAsync(IEnumerable<string> uids)
    {
        var map = await _n.GetReactionsAsync(uids);
        foreach (var item in Messages)
            if (map.TryGetValue(item.Uid, out var rows)) item.SetReactions(rows, _n.PeerId);
    }

    void AddMessage(MessageRow m)
    {
        var existing = Messages.FirstOrDefault(x => x.Uid == m.Uid);
        if (existing != null)
        {
            if (m.Status > existing.Status) existing.Status = m.Status;
            if (m.Deleted && !existing.Deleted) existing.MarkDeleted();
            return;
        }
        Messages.Add(NewItem(m));
        RebuildLayout();
    }

    [RelayCommand]
    async Task LoadOlder()
    {
        var c = Selected;
        var first = Messages.FirstOrDefault();
        if (c is null || first is null || LoadingOlder) return;
        LoadingOlder = true;
        try
        {
            var (items, more) = await _n.GetHistoryAsync(c.PeerId, first.Id);
            if (Selected != c) return;
            HasMore = more;
            for (var i = items.Count - 1; i >= 0; i--) Messages.Insert(0, NewItem(items[i]));
            RebuildLayout();
            await LoadReactionsAsync(items.Select(i => i.Uid));
            RequestScroll?.Invoke(first);
        }
        catch { Toast("Erreur", "Impossible de charger les anciens messages."); }
        finally { LoadingOlder = false; }
    }

    // =====================================================================
    //  Recherche / filtres
    // =====================================================================
    partial void OnSearchChanged(string value) => Refresh();
    partial void OnUsersModeChanged(bool value) => Refresh();
    partial void OnFilterUnreadChanged(bool value) => Refresh();

    // =====================================================================
    //  Commandes
    // =====================================================================
    [RelayCommand] void NewChat() { ShowProfile = false; ShowGroupInfo = false; UsersMode = true; }
    [RelayCommand] void CloseNewChat() { UsersMode = false; Search = ""; }
    [RelayCommand] void ClearSearch() => Search = "";
    [RelayCommand] void Back() => Selected = null;
    [RelayCommand] void CloseChat()
    {
        CancelReply();
        CancelEdit();
        Selected = null;
    }
    [RelayCommand] void AddEmoji(string? e) => Draft += e;
    [RelayCommand] Task Logout() => _logout();
    [RelayCommand] void ToggleTheme() { ThemeManager.Toggle(); IsDark = ThemeManager.IsDark; }

    void OpenItem(ConversationItem c)
    {
        Selected = c;
        if (UsersMode) UsersMode = false;
        if (HasSearch) Search = "";
    }

    partial void OnSelectedChanged(ConversationItem? oldValue, ConversationItem? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (IsRecording) _ = CancelVoice();
        Messages.Clear();
        Draft = "";
        HasMore = false;
        CancelReply();
        CancelEdit();
        ShowGroupInfo = false;
        ShowForward = false;
        OnPropertyChanged(nameof(IsGroupChat));
        if (newValue is null) return;
        newValue.IsSelected = true;
        _ = LoadHistoryAsync(newValue);
    }

    [RelayCommand]
    async Task Send()
    {
        var c = Selected;
        if (c is null) return;

        if (IsEditing) { await SaveEditAsync(); return; }

        var t = Draft.Trim();
        if (t.Length == 0) return;
        Draft = "";
        var reply = TakeReplyDraft();
        try
        {
            var row = await _n.SendAsync(c.PeerId, new MsgDraft
            {
                Text = t,
                ReplyUid = reply.Uid, ReplyAuthor = reply.Author, ReplyPreview = reply.Preview
            });
            if (row is null) { Draft = t; Toast("Erreur", "Message refusé."); return; }
            AfterSent(c, row);
        }
        catch { Draft = t; Toast("Message non envoyé", "Erreur d'enregistrement local."); }
    }

    void AfterSent(ConversationItem c, MessageRow row)
    {
        c.SetLast(row);
        if (Selected == c) AddMessage(row);
        Refresh();
        _n.Deliver(c.PeerId);
    }

    // =====================================================================
    //  Pièces jointes (images, vidéos, documents, GIF)
    // =====================================================================
    [RelayCommand] async Task AttachImage() => await AttachAsync(PickKind.Image);
    [RelayCommand] async Task AttachVideo() => await AttachAsync(PickKind.Video);
    [RelayCommand] async Task AttachDocument() => await AttachAsync(PickKind.Document);

    async Task AttachAsync(PickKind kind)
    {
        var c = Selected;
        if (c is null) return;
        var path = PickFile?.Invoke(kind);
        if (string.IsNullOrEmpty(path)) return;
        await SendMediaAsync(path);
    }

    // Appelée aussi par le glisser-déposer de la vue
    public async Task SendMediaAsync(string path)
    {
        var c = Selected;
        if (c is null || !File.Exists(path)) return;
        try
        {
            var kind = MediaStore.KindOf(path);
            var (id, size) = MediaStore.Import(path);
            var reply = TakeReplyDraft();
            var row = await _n.SendAsync(c.PeerId, new MsgDraft
            {
                Kind = kind,
                Text = Draft.Trim(),
                MediaId = id,
                MediaName = Path.GetFileName(path),
                MediaSize = size,
                MediaMime = MediaStore.MimeOf(path),
                ReplyUid = reply.Uid, ReplyAuthor = reply.Author, ReplyPreview = reply.Preview
            });
            if (row is null) { Toast("Envoi impossible", "Ce fichier ne peut pas être envoyé."); return; }
            Draft = "";
            AfterSent(c, row);
        }
        catch (InvalidOperationException)
        {
            Toast("Fichier trop volumineux", $"La limite est de {Limits.MaxMediaBytes / (1024 * 1024)} Mo par pièce jointe.");
        }
        catch { Toast("Envoi impossible", "Impossible de lire ce fichier."); }
    }

    [RelayCommand]
    async Task SendSticker(string? emoji)
    {
        var c = Selected;
        if (c is null || string.IsNullOrEmpty(emoji)) return;
        var reply = TakeReplyDraft();
        var row = await _n.SendAsync(c.PeerId, new MsgDraft
        {
            Kind = Core.Kind.Sticker, Text = emoji,
            ReplyUid = reply.Uid, ReplyAuthor = reply.Author, ReplyPreview = reply.Preview
        });
        if (row != null) AfterSent(c, row);
    }

    // =====================================================================
    //  Messages vocaux
    // =====================================================================
    [RelayCommand]
    void StartVoice()
    {
        if (Selected is null || IsRecording) return;
        var err = _recorder.Start();
        if (err != null) { Toast("Micro indisponible", err); return; }
        IsRecording = true;
        RecordTime = "0:00";
        _recordTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _recordTimer.Tick -= OnRecordTick;
        _recordTimer.Tick += OnRecordTick;
        _recordTimer.Start();
    }

    void OnRecordTick(object? sender, EventArgs e)
    {
        var el = _recorder.Elapsed;
        RecordTime = el.TotalHours >= 1 ? el.ToString(@"h\:mm\:ss") : el.ToString(@"m\:ss");
        if (el.TotalSeconds > 300) _ = StopVoice(send: true);   // 5 minutes maximum
    }

    [RelayCommand] Task StopVoiceSend() => StopVoice(send: true);
    [RelayCommand] Task CancelVoice() => StopVoice(send: false);

    async Task StopVoice(bool send)
    {
        if (!IsRecording) return;
        IsRecording = false;
        _recordTimer?.Stop();
        var res = _recorder.Stop();
        RecordTime = "0:00";
        if (!send || res is null || Selected is null) return;

        var (temp, duration, _) = res.Value;
        if (duration < 500) { try { File.Delete(temp); } catch { } Toast("Message vocal", "Enregistrement trop court."); return; }
        try
        {
            var (id, size) = MediaStore.Import(temp);
            var reply = TakeReplyDraft();
            var row = await _n.SendAsync(Selected.PeerId, new MsgDraft
            {
                Kind = Core.Kind.Voice, MediaId = id, MediaName = "message-vocal.wav",
                MediaSize = size, MediaMime = "audio/wav", DurationMs = duration,
                ReplyUid = reply.Uid, ReplyAuthor = reply.Author, ReplyPreview = reply.Preview
            });
            if (row != null) AfterSent(Selected, row);
        }
        catch { Toast("Envoi impossible", "Le message vocal n'a pas pu être envoyé."); }
        finally { try { File.Delete(temp); } catch { } }
    }

    // =====================================================================
    //  Répondre / modifier / transférer / réagir
    // =====================================================================
    void BeginReply(MessageItem m)
    {
        if (Selected is null) return;
        _replyItem = m;
        ReplyAuthor = m.IsMine ? "Vous" : (Selected.IsGroup ? m.SenderName : Selected.Name);
        ReplyPreview = MsgPreview.Of(new MessageRow { Kind = m.Kind, Text = m.Text, MediaName = m.MediaName });
        HasReply = true;
        CancelEdit();
        RequestFocusComposer?.Invoke();
    }

    [RelayCommand] void CancelReply() { _replyItem = null; HasReply = false; ReplyAuthor = ""; ReplyPreview = ""; }

    (string Uid, string Author, string Preview) TakeReplyDraft()
    {
        if (!HasReply || _replyItem is null) return ("", "", "");
        var r = (_replyItem.Uid, ReplyAuthor, ReplyPreview);
        CancelReply();
        return r;
    }

    void BeginEdit(MessageItem m)
    {
        if (Selected is null || !m.IsMine) return;
        _editingItem = m;
        IsEditing = true;
        EditingPreview = m.Text.Length > 90 ? m.Text.Substring(0, 90) + "…" : m.Text;
        Draft = m.Text;
        CancelReply();
        RequestFocusComposer?.Invoke();
    }

    [RelayCommand]
    void CancelEdit()
    {
        if (IsEditing && _editingItem != null) Draft = "";
        _editingItem = null;
        IsEditing = false;
        EditingPreview = "";
    }

    async Task SaveEditAsync()
    {
        var m = _editingItem;
        var t = Draft.Trim();
        if (m is null) { CancelEdit(); return; }
        if (t.Length == 0) { Toast("Modification", "Un message ne peut pas être vide."); return; }
        try
        {
            var r = await _n.EditMessageAsync(m.Id, t);
            if (r != "ok") { Toast("Modification impossible", "Ce message ne peut pas être modifié."); return; }
            m.ApplyEdit(t, DateTime.UtcNow);
            _editingItem = null;
            IsEditing = false;
            EditingPreview = "";
            Draft = "";
            if (Selected is { } c) await RefreshLastAsync(c);
        }
        catch { Toast("Modification impossible", "Erreur d'enregistrement local."); }
    }

    async Task ReactAsync(MessageItem m, string emoji)
    {
        try { await _n.ToggleReactionAsync(m.Id, m.Uid, emoji); }
        catch { Toast("Réaction", "Impossible d'ajouter cette réaction."); }
    }

    async Task RefreshReactionsAsync(string uid)
    {
        var map = await _n.GetReactionsAsync(new[] { uid });
        var item = Messages.FirstOrDefault(x => x.Uid == uid);
        if (item is null) return;
        item.SetReactions(map.TryGetValue(uid, out var rows) ? rows : new List<ReactionRow>(), _n.PeerId);
    }

    void BeginForward(MessageItem m)
    {
        _forwarding = m;
        ForwardSearch = "";
        BuildForwardTargets();
        ShowForward = true;
    }

    void BuildForwardTargets()
    {
        ForwardTargets.Clear();
        var q = ForwardSearch.Trim();
        foreach (var c in _known.Values.OrderByDescending(c => c.LastAt))
        {
            if (Selected != null && c.PeerId == Selected.PeerId) continue;
            if (q.Length > 0 && !Match(c, q)) continue;
            ForwardTargets.Add(c);
        }
    }

    partial void OnForwardSearchChanged(string value) => BuildForwardTargets();

    [RelayCommand] void CloseForward() { ShowForward = false; _forwarding = null; }

    [RelayCommand]
    async Task ForwardTo(ConversationItem? c)
    {
        var m = _forwarding;
        if (c is null || m is null) return;
        ShowForward = false;
        _forwarding = null;
        try
        {
            var row = await _n.SendAsync(c.PeerId, new MsgDraft
            {
                Kind = m.Kind, Text = m.Text, MediaId = m.MediaId, MediaName = m.MediaName,
                MediaSize = m.MediaSize, MediaMime = m.MediaMime, DurationMs = m.DurationMs, Forwarded = true
            });
            if (row is null) { Toast("Transfert impossible", "Ce message ne peut pas être transféré."); return; }
            c.SetLast(row);
            if (Selected == c) AddMessage(row);
            Refresh();
            _n.Deliver(c.PeerId);
            Toast("Message transféré", $"Vers {c.Name}.");
        }
        catch { Toast("Transfert impossible", "Erreur d'enregistrement local."); }
    }

    void OpenMediaFile(MessageItem m)
    {
        if (m.MediaPath is null || !File.Exists(m.MediaPath)) { Toast("Fichier absent", "La pièce jointe n'est pas encore téléchargée."); return; }
        try { Process.Start(new ProcessStartInfo(m.MediaPath) { UseShellExecute = true }); }
        catch { Toast("Ouverture impossible", "Aucune application ne peut ouvrir ce fichier."); }
    }

    async Task RetryMediaAsync(MessageItem m)
    {
        var ok = await _n.RetryMediaAsync(m.Id);
        m.RefreshMedia();
        if (!ok) Toast("Téléchargement impossible", "L'expéditeur n'est pas joignable pour le moment.");
    }

    void OnMediaReady(string uid)
    {
        Messages.FirstOrDefault(m => m.Uid == uid)?.RefreshMedia();
    }

    void OnEdited(MessageRow m)
    {
        var item = Messages.FirstOrDefault(x => x.Uid == m.Uid);
        item?.ApplyEdit(m.Text, m.EditedAt);
        if (_known.TryGetValue(m.ContactId, out var c) && c.LastUid == m.Uid) c.SetLast(m);
    }

    void RefreshPlayback()
    {
        foreach (var m in Messages) m.RefreshPlayback();
    }

    // =====================================================================
    //  Groupes
    // =====================================================================
    [RelayCommand]
    void NewGroup()
    {
        ShowProfile = false;
        ShowGroupInfo = false;
        UsersMode = false;
        NewGroupName = "";
        GroupPicks.Clear();
        foreach (var c in _known.Values.Where(c => !c.IsGroup).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            GroupPicks.Add(new ContactChoice { PeerId = c.PeerId, Name = c.Name, Photo = c.Photo });
        ShowNewGroup = true;
    }

    [RelayCommand] void CloseNewGroup() => ShowNewGroup = false;

    [RelayCommand]
    async Task CreateGroup()
    {
        var name = NewGroupName.Trim();
        if (name.Length < 2) { Toast("Nouveau groupe", "Donnez un nom au groupe (2 caractères minimum)."); return; }
        var picked = GroupPicks.Where(g => g.IsSelected).Select(g => g.PeerId).ToList();
        if (picked.Count == 0) { Toast("Nouveau groupe", "Choisissez au moins un participant."); return; }
        try
        {
            var contact = await _n.CreateGroupAsync(name, picked);
            ShowNewGroup = false;
            var item = Item(contact);
            _known[contact.PeerId] = item;
            Refresh();
            OpenItem(item);
        }
        catch { Toast("Nouveau groupe", "Création impossible."); }
    }

    [RelayCommand]
    async Task OpenGroupInfo()
    {
        if (Selected is not { IsGroup: true } c) return;
        GroupTitle = c.Name;
        AddingMembers = false;
        await LoadGroupMembersAsync(c.PeerId);
        ShowGroupInfo = true;
    }

    [RelayCommand] void CloseGroupInfo() { ShowGroupInfo = false; AddingMembers = false; }

    async Task LoadGroupMembersAsync(string groupId)
    {
        GroupMembers.Clear();
        var members = await _n.GetGroupMembersAsync(groupId);
        foreach (var m in members)
            GroupMembers.Add(new GroupMemberItem
            {
                PeerId = m.PeerId, Name = m.IsMe ? _n.DisplayName + " (vous)" : m.Name,
                IsAdmin = m.IsAdmin, IsMe = m.IsMe, Online = !m.IsMe && _n.IsOnline(m.PeerId)
            });
        IAmGroupAdmin = members.Any(m => m.IsMe && m.IsAdmin);
        var ids = members.Select(m => m.PeerId).ToHashSet();
        AddMemberPicks.Clear();
        foreach (var c in _known.Values.Where(c => !c.IsGroup && !ids.Contains(c.PeerId)).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            AddMemberPicks.Add(new ContactChoice { PeerId = c.PeerId, Name = c.Name, Photo = c.Photo });
    }

    [RelayCommand] void ToggleAddMembers() => AddingMembers = !AddingMembers;

    [RelayCommand]
    async Task AddSelectedMembers()
    {
        if (Selected is not { IsGroup: true } c) return;
        var picks = AddMemberPicks.Where(p => p.IsSelected).Select(p => p.PeerId).ToList();
        if (picks.Count == 0) return;
        var err = await _n.AddGroupMembersAsync(c.PeerId, picks);
        if (err.Length > 0) { Toast("Groupe", err); return; }
        AddingMembers = false;
        await LoadGroupMembersAsync(c.PeerId);
        Toast("Groupe", "Membres ajoutés.");
    }

    [RelayCommand]
    async Task RemoveMember(GroupMemberItem? m)
    {
        if (m is null || Selected is not { IsGroup: true } c || m.IsMe) return;
        var ok = MessageBox.Show($"Retirer {m.Name} du groupe ?", "ChatLan", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;
        var err = await _n.RemoveGroupMemberAsync(c.PeerId, m.PeerId);
        if (err.Length > 0) { Toast("Groupe", err); return; }
        await LoadGroupMembersAsync(c.PeerId);
    }

    [RelayCommand]
    async Task ToggleAdmin(GroupMemberItem? m)
    {
        if (m is null || m.IsMe || Selected is not { IsGroup: true } c) return;
        var err = await _n.SetGroupAdminAsync(c.PeerId, m.PeerId, !m.IsAdmin);
        if (err.Length > 0) { Toast("Groupe", err); return; }
        await LoadGroupMembersAsync(c.PeerId);
    }

    [RelayCommand]
    async Task LeaveGroup()
    {
        if (Selected is not { IsGroup: true } c) return;
        var ok = MessageBox.Show($"Quitter le groupe « {c.Name} » ?", "ChatLan", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;
        await _n.LeaveGroupAsync(c.PeerId);
        _known.Remove(c.PeerId);
        Selected = null;
        ShowGroupInfo = false;
        await ReloadAsync();
    }

    // =====================================================================
    //  Appels audio / vidéo / de groupe
    // =====================================================================
    [RelayCommand] Task StartAudioCall() => StartCallAsync(video: false);
    [RelayCommand] Task StartVideoCall() => StartCallAsync(video: true);

    async Task StartCallAsync(bool video)
    {
        var c = Selected;
        if (c is null) return;
        if (_n.Calls.AnyActive) { Toast("Appel", "Un appel est déjà en cours."); return; }

        var targets = new List<(string PeerId, string Name)>();
        if (c.IsGroup)
        {
            var members = await _n.GetGroupMembersAsync(c.PeerId);
            targets.AddRange(members.Where(m => !m.IsMe && _n.IsOnline(m.PeerId)).Select(m => (m.PeerId, m.Name)));
            if (targets.Count == 0) { Toast("Appel de groupe", "Aucun membre du groupe n'est en ligne."); return; }
        }
        else
        {
            if (!_n.IsOnline(c.PeerId)) { Toast("Appel impossible", $"{c.Name} n'est pas en ligne."); return; }
            targets.Add((c.PeerId, c.Name));
        }

        CallTitle = c.IsGroup ? c.Name : c.Name;
        CallVideo = video;
        CallIncoming = false;
        CallIsGroup = c.IsGroup;
        CallNote = "";
        ShowCall = true;
        CallParticipants.Clear();
        _n.Calls.StartOutgoing(targets, video);
        StartCallTimer();
        RefreshCall();
    }

    void OnIncomingCall(CallInviteDto dto)
    {
        CallVideo = dto.Video;
        CallIsGroup = dto.Peers.Length > 2;
        CallTitle = CallIsGroup ? "Appel de groupe" : dto.FromName;
        CallNote = "";
        CallStatus = "Appel entrant";
        CallIncoming = true;
        CallParticipants.Clear();
        ShowCall = true;
        StartCallTimer();
        RefreshCall();
        Notifier.Alert();
    }

    [RelayCommand]
    void AnswerCall()
    {
        if (!_n.Calls.AcceptIncoming()) { ShowCall = false; return; }
        CallIncoming = false;
        StartCallTimer();
        RefreshCall();
    }

    [RelayCommand]
    void DeclineCall()
    {
        _n.Calls.DeclineIncoming();
        ShowCall = false;
        CallIncoming = false;
        StopCallTimer();
    }

    [RelayCommand]
    void HangupCall()
    {
        _n.Calls.Hangup();
        ShowCall = false;
        CallIncoming = false;
        StopCallTimer();
    }

    [RelayCommand]
    void ToggleMuteCall()
    {
        _n.Calls.ToggleMute();
        CallMuted = _n.Calls.Muted;
    }

    [RelayCommand]
    void ToggleCameraCall()
    {
        _n.Calls.ToggleCamera();
        CallCameraOn = _n.Calls.CameraOn;
    }

    void OnCallEnded(string reason)
    {
        ShowCall = false;
        CallIncoming = false;
        StopCallTimer();
        if (!string.IsNullOrEmpty(reason)) Toast("Appel", reason);
        RefreshCall();
    }

    void StartCallTimer()
    {
        _callTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _callTimer.Tick -= OnCallTick;
        _callTimer.Tick += OnCallTick;
        _callTimer.Start();
    }

    void StopCallTimer() => _callTimer?.Stop();

    void OnCallTick(object? sender, EventArgs e)
    {
        if (!ShowCall) { StopCallTimer(); return; }
        CallStatus = _n.Calls.Phase switch
        {
            CallPhase.Ringing => CallIncoming ? "Appel entrant" : "Sonnerie…",
            CallPhase.Active => FormatCallTime(_n.Calls.Elapsed),
            _ => ""
        };
        CallMuted = _n.Calls.Muted;
        CallCameraOn = _n.Calls.CameraOn;
        CallNote = _n.Calls.Note ?? "";
        RefreshCallFrames();
    }

    static string FormatCallTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    void RefreshCall()
    {
        if (_n.Calls.Phase == CallPhase.Idle)
        {
            if (!CallIncoming) { ShowCall = false; StopCallTimer(); }
            return;
        }
        var peers = _n.Calls.Peers;
        foreach (var p in peers)
        {
            var item = CallParticipants.FirstOrDefault(x => x.PeerId == p.PeerId);
            if (item is null)
            {
                item = new CallPeerItem
                {
                    PeerId = p.PeerId, Name = p.IsMe ? "Vous" : p.Name, IsMe = p.IsMe
                };
                CallParticipants.Add(item);
            }
            item.IsActive = p.IsMe || p.Accepted;
            item.StatusText = p.IsMe ? "vous" : p.Accepted ? "en communication" : "sonne…";
        }
        foreach (var stale in CallParticipants.Where(x => !peers.Any(p => p.PeerId == x.PeerId)).ToList())
            CallParticipants.Remove(stale);

        // Appel à deux : l'autre personne occupe tout l'écran, ma vignette passe dans le coin (comme WhatsApp).
        // Appel de groupe : grille qui remplit l'écran.
        var oneToOne = CallParticipants.Count == 2 && CallParticipants.Any(x => x.IsMe);
        foreach (var it in CallParticipants)
            it.Mode = oneToOne ? (it.IsMe ? CallTileMode.Pip : CallTileMode.Full) : CallTileMode.Tile;
        RefreshCallFrames();
    }

    void RefreshCallFrames()
    {
        foreach (var p in _n.Calls.Peers)
        {
            var item = CallParticipants.FirstOrDefault(x => x.PeerId == p.PeerId);
            if (item is null) continue;
            if (p.Frame is { Length: > 0 } bytes && item.FrameVersion != p.FrameVersion)
            {
                item.FrameVersion = p.FrameVersion;
                try { item.FrameSource = Img.FromBytes(bytes); item.HasFrame = true; } catch { }
            }
            if (p.Frame is null) item.HasFrame = false;
        }
    }

    // ----- Connexion manuelle -----
    [RelayCommand]
    async Task ConnectManual()
    {
        if (ConnectBusy) return;
        ConnectBusy = true;
        try
        {
            var err = await _n.ConnectManualAsync(ConnectAddress);
            if (err != null) Toast("Connexion impossible", err);
            else { Toast("Connecté", "L'autre ordinateur a été ajouté à vos contacts."); ConnectAddress = ""; }
        }
        finally { ConnectBusy = false; }
    }

    // ----- Suppression -----
    async Task DeleteForMeAsync(MessageItem m)
    {
        try
        {
            if (!await _n.DeleteForMeAsync(m.Id)) return;
            Messages.Remove(m);
            RebuildLayout();
            if (Selected is { } c) await RefreshLastAsync(c);
        }
        catch { Toast("Erreur", "Suppression impossible."); }
    }

    async Task DeleteForEveryoneAsync(MessageItem m)
    {
        try
        {
            var r = await _n.DeleteForEveryoneAsync(m.Id);
            if (r == "ok") OnDeletedForEveryone(m.Uid);
            else if (r == "too_late")
                Toast("Suppression impossible",
                      $"Un message ne peut être supprimé pour tous que pendant {Limits.DeleteForEveryoneMinutes} minutes. Utilisez « Supprimer pour moi ».");
            else Toast("Suppression impossible", "Ce message ne peut pas être supprimé.");
        }
        catch { Toast("Erreur", "Suppression impossible."); }
    }

    async Task RefreshLastAsync(ConversationItem c)
    {
        var (items, _) = await _n.GetHistoryAsync(c.PeerId, null);
        if (items.Count > 0) c.SetLast(items[items.Count - 1]);
        else c.ClearConversation();
        Refresh();
    }

    async Task DeleteChatAsync(ConversationItem c)
    {
        var text = c.IsGroup
            ? $"Supprimer la discussion du groupe « {c.Name} » ?\nLe groupe sera également quitté."
            : $"Supprimer la discussion avec {c.Name} ?\nLes messages seront supprimés de cet ordinateur uniquement.";
        var ok = MessageBox.Show(text, "ChatLan", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;
        try
        {
            if (c.IsGroup) await _n.LeaveGroupAsync(c.PeerId);
            else await _n.DeleteConversationAsync(c.PeerId);
            if (Selected == c) Selected = null;
            _known.Remove(c.PeerId);
            c.ClearConversation();
            Refresh();
        }
        catch { Toast("Erreur", "Suppression impossible."); }
    }

    [RelayCommand]
    async Task DeleteCurrentChat()
    {
        if (Selected is { } c) await DeleteChatAsync(c);
    }

    // =====================================================================
    //  Événements du réseau pair-à-pair
    // =====================================================================
    void OnContactChanged(ContactRow c)
    {
        Item(c);
        if (c.IsGroup && Selected?.PeerId == c.PeerId && ShowGroupInfo) _ = LoadGroupMembersAsync(c.PeerId);
        Refresh();
    }

    void OnReceived(MessageRow m)
    {
        if (!_known.TryGetValue(m.ContactId, out var c)) { _ = ReloadAsync().ContinueWith(_ => UI(Refresh)); return; }
        c.Typing = false;
        c.SetLast(m);
        var active = Application.Current?.MainWindow?.IsActive == true;
        var text = m.Kind switch
        {
            Core.Kind.Image => "📷 Photo",
            Core.Kind.Video => "🎬 Vidéo",
            Core.Kind.Voice => "🎤 Message vocal",
            Core.Kind.Audio => "🎵 Audio",
            Core.Kind.File => "📄 " + m.MediaName,
            Core.Kind.Sticker => m.Text + " Sticker",
            _ => m.Text
        };

        if (Selected == c)
        {
            AddMessage(m);
            if (active) _ = _n.MarkReadAsync(c.PeerId);
            else { c.Unread++; Notify(c, m, text); }
        }
        else { c.Unread++; Notify(c, m, text); }
        Refresh();
    }

    void OnStatus(string[] uids, int st)
    {
        var set = uids.ToHashSet();
        foreach (var m in Messages)
            if (m.IsMine && set.Contains(m.Uid) && m.Status < st) m.Status = st;
        foreach (var c in _known.Values)
            if (c.Mine && set.Contains(c.LastUid) && c.Status < st) c.Status = st;
    }

    void OnPresence(string id, bool on, DateTime? seen)
    {
        if (!_known.TryGetValue(id, out var c)) return;
        c.Online = on;
        if (!on && seen is { } s) c.LastSeen = s;
        if (UsersMode) Refresh();
    }

    void OnDeletedForEveryone(string uid)
    {
        Messages.FirstOrDefault(m => m.Uid == uid)?.MarkDeleted();
        foreach (var c in _known.Values) if (c.LastUid == uid) c.MarkDeleted();
    }

    public void OnActivated()
    {
        if (Selected is { Unread: > 0 } c)
        {
            c.Unread = 0;
            RefreshUnread();
            _ = _n.MarkReadAsync(c.PeerId);
        }
    }

    // =====================================================================
    //  Profil
    // =====================================================================
    void LoadMyPhoto()
    {
        var bytes = _n.Avatar;
        try { MePhoto = bytes is { Length: > 0 } ? Img.FromBytes(bytes) : null; } catch { MePhoto = null; }
    }

    [RelayCommand]
    void OpenProfile()
    {
        EditName = MeName; EditAbout = MeAbout;
        ProfileMessage = ""; ProfileMessageIsError = false;
        ShowProfile = true;
    }

    [RelayCommand] void CloseProfile() => ShowProfile = false;

    [RelayCommand]
    async Task SaveProfile()
    {
        if (ProfileBusy) return;
        ProfileBusy = true;
        try
        {
            var err = await _n.UpdateProfileAsync(EditName, EditAbout);
            if (err != null) { ProfileMessage = err; ProfileMessageIsError = true; return; }
            MeName = _n.DisplayName; MeAbout = _n.About;
            ProfileMessage = "Profil enregistré et partagé."; ProfileMessageIsError = false;
        }
        catch { ProfileMessage = "Enregistrement impossible."; ProfileMessageIsError = true; }
        finally { ProfileBusy = false; }
    }

    public async Task ChangePhotoAsync(string path)
    {
        ProfileBusy = true;
        try
        {
            var bytes = Img.ToJpeg(path);
            await _n.SetAvatarAsync(bytes);
            MePhoto = Img.FromBytes(bytes);
            ProfileMessage = "Photo mise à jour."; ProfileMessageIsError = false;
        }
        catch { ProfileMessage = "Impossible de lire cette image."; ProfileMessageIsError = true; }
        finally { ProfileBusy = false; }
    }

    [RelayCommand]
    async Task RemovePhoto()
    {
        ProfileBusy = true;
        try
        {
            await _n.SetAvatarAsync(null);
            MePhoto = null;
            ProfileMessage = "Photo supprimée."; ProfileMessageIsError = false;
        }
        catch { ProfileMessage = "Opération impossible."; ProfileMessageIsError = true; }
        finally { ProfileBusy = false; }
    }

    // =====================================================================
    //  Notifications
    // =====================================================================
    void Notify(ConversationItem c, MessageRow m, string text)
    {
        Notifier.Alert();
        _toastTarget = c;
        var title = c.Name;
        if (c.IsGroup && m.FromName.Length > 0) title = c.Name + " — " + m.FromName;
        ToastTitle = title; ToastText = text; ToastPhoto = c.Photo; ToastVisible = true;
        HideToastLater();
    }

    void Toast(string title, string text)
    {
        _toastTarget = null;
        ToastTitle = title; ToastText = text; ToastPhoto = null; ToastVisible = true;
        HideToastLater();
    }

    async void HideToastLater()
    {
        var t = ++_toastTick;
        await Task.Delay(4500);
        if (t == _toastTick) ToastVisible = false;
    }

    [RelayCommand] void DismissToast() => ToastVisible = false;

    [RelayCommand]
    void OpenToast()
    {
        ToastVisible = false;
        if (_toastTarget != null) OpenItem(_toastTarget);
    }
}

// ---------- Choix d'un contact (nouveau groupe, ajout de membre) ----------
public partial class ContactChoice : ObservableObject
{
    public string PeerId { get; init; } = "";
    public string Name { get; init; } = "";
    public ImageSource? Photo { get; init; }
    [ObservableProperty] bool _isSelected;
}

// ---------- Un membre dans la fiche d'un groupe ----------
public partial class GroupMemberItem : ObservableObject
{
    public string PeerId { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsMe { get; init; }
    public bool Online { get; init; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RoleText))] bool _isAdmin;
    public string RoleText => IsMe ? (IsAdmin ? "vous · administrateur" : "vous") : IsAdmin ? "administrateur" : Online ? "en ligne" : "membre";
}

// ---------- Une vignette pendant un appel ----------
// Tile = case d'une grille (appel de groupe) ; Full = l'autre personne en plein écran ; Pip = ma vignette en coin
public enum CallTileMode { Tile, Full, Pip }

public partial class CallPeerItem : ObservableObject
{
    public string PeerId { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsMe { get; init; }
    [ObservableProperty] ImageSource? _frameSource;
    [ObservableProperty] bool _hasFrame;
    [ObservableProperty] bool _isActive;
    [ObservableProperty] string _statusText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TileCorner), nameof(TileBorder), nameof(CaptionCorner),
                                                  nameof(AvatarSize), nameof(ShowCaption), nameof(ShowStatus))]
    CallTileMode _mode = CallTileMode.Tile;
    public int FrameVersion { get; set; }

    public System.Windows.CornerRadius TileCorner =>
        Mode == CallTileMode.Full ? new System.Windows.CornerRadius(0) : new System.Windows.CornerRadius(16);
    public System.Windows.Thickness TileBorder =>
        Mode == CallTileMode.Full ? new System.Windows.Thickness(0) : new System.Windows.Thickness(1);
    public System.Windows.CornerRadius CaptionCorner =>
        Mode == CallTileMode.Full ? new System.Windows.CornerRadius(0) : new System.Windows.CornerRadius(0, 0, 15, 15);
    public double AvatarSize => Mode switch { CallTileMode.Full => 168, CallTileMode.Pip => 54, _ => 104 };
    public bool ShowCaption => Mode != CallTileMode.Full;
    public bool ShowStatus => Mode == CallTileMode.Tile;
}

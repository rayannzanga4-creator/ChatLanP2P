using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using ChatLan.Core;
using ChatLan.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatLan.ViewModels;

// ===========================================================================
//  Statuts / stories : bande horizontale, composeur et visionneuse.
// ===========================================================================
public partial class ShellViewModel
{
    public ObservableCollection<StatusGroup> StatusGroups { get; } = new();
    public ObservableCollection<StatusGroup> OtherStatusGroups { get; } = new();
    public ObservableCollection<StatusSegment> StatusSegments { get; } = new();
    public ObservableCollection<StatusViewer> StatusViewerList { get; } = new();
    public ObservableCollection<ConversationItem> StatusShareTargets { get; } = new();
    public ObservableCollection<ContactChoice> StatusAudiencePicks { get; } = new();

    // Emojis proposés pour réagir / insérer dans un statut
    public string[] StatusEmojis { get; } = EmojiData.Split("👍❤️😂😮😢🙏🔥🎉😍🤩😎🥳💯🙌👏✨");
    public ObservableCollection<StatusColorOption> StatusColorOptions { get; } = BuildColorOptions();

    static ObservableCollection<StatusColorOption> BuildColorOptions() =>
        new(Enumerable.Range(0, StatusPalette.Count)
            .Select(i => new StatusColorOption { Index = i, Brush = StatusPalette.BrushAt(i), IsSelected = i == 0 }));

    [ObservableProperty] bool _hasStatusFeed;

    // ----- composeur -----
    [ObservableProperty] bool _showStatusComposer;
    [ObservableProperty] string _statusText = "";
    [ObservableProperty] string _statusKind = Core.Kind.Text;
    [ObservableProperty] string? _statusSourcePath;
    [ObservableProperty] ImageSource? _statusPhoto;
    [ObservableProperty] string _statusMediaLabel = "";
    [ObservableProperty] bool _statusHasMedia;
    [ObservableProperty] int _statusColor;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusAudienceContacts))] bool _statusAudienceOnly;

    // Case « Mes contacts » (l'inverse de StatusAudienceOnly), pour les cases à cocher XAML
    public bool StatusAudienceContacts
    {
        get => !StatusAudienceOnly;
        set { if (value) StatusAudienceOnly = false; }
    }
    [ObservableProperty] bool _statusPublishing;

    // ----- visionneuse -----
    [ObservableProperty] bool _showStatusViewer;
    [ObservableProperty] StatusGroup? _statusGroup;
    [ObservableProperty] StatusItem? _currentStatus;
    [ObservableProperty] int _statusIndex;
    [ObservableProperty] bool _showStatusViewers;
    [ObservableProperty] bool _showStatusShare;
    [ObservableProperty] string _statusReply = "";

    // =====================================================================
    //  Chargement
    // =====================================================================
    public async Task LoadStatusesAsync()
    {
        try
        {
            var feed = await _n.GetStatusFeedAsync();
            var groups = new List<StatusGroup>();

            var mine = new StatusGroup { PeerId = _n.PeerId, Name = MeName, IsMine = true, Photo = MePhoto };
            foreach (var info in feed.Where(f => f.Status.FromMe).OrderBy(f => f.Status.CreatedAt))
                mine.Items.Add(ToStatusItem(info));
            groups.Add(mine);

            foreach (var byAuthor in feed.Where(f => !f.Status.FromMe).GroupBy(f => f.Status.AuthorPeerId))
            {
                var first = byAuthor.First().Status;
                var g = new StatusGroup
                {
                    PeerId = first.AuthorPeerId,
                    Name = first.AuthorName,
                    Photo = _known.TryGetValue(first.AuthorPeerId, out var c) ? c.Photo : null
                };
                foreach (var info in byAuthor.OrderBy(f => f.Status.CreatedAt)) g.Items.Add(ToStatusItem(info));
                groups.Add(g);
            }

            StatusGroups.Clear();
            OtherStatusGroups.Clear();
            foreach (var g in groups.Where(g => g.IsMine)
                         .Concat(groups.Where(g => !g.IsMine)
                             .OrderByDescending(g => g.HasUnseen)
                             .ThenByDescending(g => g.Items.Count > 0 ? g.Items[^1].At : DateTime.MinValue)))
            {
                g.OpenRequested = OpenStatus;
                StatusGroups.Add(g);
                if (!g.IsMine && g.Items.Count > 0) OtherStatusGroups.Add(g);
            }
            HasStatusFeed = OtherStatusGroups.Count > 0;

            // La visionneuse est ouverte : on retrouve le statut courant
            if (ShowStatusViewer && StatusGroup is { } open)
            {
                var again = StatusGroups.FirstOrDefault(x => x.PeerId == open.PeerId);
                if (again is null || again.Items.Count == 0) { ShowStatusViewer = false; return; }
                var keepId = CurrentStatus?.Id;
                StatusGroup = again;
                var idx = again.Items.FindIndex(i => i.Id == keepId);
                StatusIndex = idx >= 0 ? idx : Math.Min(StatusIndex, again.Items.Count - 1);
                CurrentStatus = again.Items[StatusIndex];
                RebuildSegments();
            }
        }
        catch { }
    }

    StatusItem ToStatusItem(StatusInfo info)
    {
        var s = info.Status;
        var item = new StatusItem
        {
            Id = s.Id, AuthorPeerId = s.AuthorPeerId, AuthorName = s.AuthorName, Kind = s.Kind, Text = s.Text,
            MediaId = s.MediaId, MediaName = s.MediaName, MediaSize = s.MediaSize, Background = s.Background,
            At = s.CreatedAt, IsMine = s.FromMe, ViewCount = info.ViewCount, SeenByMe = info.SeenByMe,
            MyReaction = info.MyReaction, Viewers = info.Viewers
        };
        foreach (var (emoji, count) in info.Reactions)
            item.Reactions.Add(new ReactionChip { Emoji = emoji, Count = count, Mine = emoji == info.MyReaction });
        item.MediaPath = MediaStore.Exists(s.MediaId) ? MediaStore.PathOf(s.MediaId) : null;
        if (s.Kind == Core.Kind.Image && item.MediaPath != null)
        {
            try { item.Photo = Img.FromFile(item.MediaPath); } catch { }
        }
        return item;
    }

    async Task RefreshStatusMediaAsync(string statusId)
    {
        var item = StatusGroups.SelectMany(g => g.Items).FirstOrDefault(i => i.Id == statusId);
        if (item is null) { await LoadStatusesAsync(); return; }
        item.MediaPath = MediaStore.Exists(item.MediaId) ? MediaStore.PathOf(item.MediaId) : null;
        if (item.IsImage && item.MediaPath != null)
        {
            try { item.Photo = Img.FromFile(item.MediaPath); } catch { }
        }
    }

    // =====================================================================
    //  Bande des statuts
    // =====================================================================
    [RelayCommand]
    void OpenMyStatus()
    {
        var mine = StatusGroups.FirstOrDefault(g => g.IsMine);
        if (mine is null || mine.Items.Count == 0) { OpenStatusComposer(); return; }
        OpenStatus(mine);
    }

    void OpenStatus(StatusGroup group)
    {
        if (group.Items.Count == 0) { OpenStatusComposer(); return; }
        StatusGroup = group;
        StatusIndex = 0;
        StatusReply = "";
        ShowStatusViewers = false;
        ShowStatusShare = false;
        ShowStatusViewer = true;
        CurrentStatus = group.Items[0];
        RebuildSegments();
        MarkSeen();
    }

    void MarkSeen()
    {
        if (CurrentStatus is null || CurrentStatus.IsMine || CurrentStatus.SeenByMe) return;
        CurrentStatus.SeenByMe = true;
        _ = _n.MarkStatusSeenAsync(CurrentStatus.Id);
    }

    void RebuildSegments()
    {
        StatusSegments.Clear();
        if (StatusGroup is null) return;
        for (var i = 0; i < StatusGroup.Items.Count; i++)
            StatusSegments.Add(new StatusSegment { Done = i < StatusIndex, Active = i == StatusIndex });
    }

    void GoToStatus(int index)
    {
        if (StatusGroup is null || index < 0 || index >= StatusGroup.Items.Count) return;
        StatusIndex = index;
        CurrentStatus = StatusGroup.Items[index];
        ShowStatusViewers = false;
        ShowStatusShare = false;
        RebuildSegments();
        MarkSeen();
    }

    [RelayCommand]
    void NextStatus()
    {
        if (StatusGroup is null) return;
        if (StatusIndex + 1 < StatusGroup.Items.Count) { GoToStatus(StatusIndex + 1); return; }
        var i = StatusGroups.IndexOf(StatusGroup);
        if (i >= 0 && i + 1 < StatusGroups.Count && StatusGroups[i + 1].Items.Count > 0)
        {
            var next = StatusGroups[i + 1];
            OpenStatus(next);
        }
        else CloseStatus();
    }

    [RelayCommand]
    void PreviousStatus()
    {
        if (StatusGroup is null) return;
        if (StatusIndex > 0) { GoToStatus(StatusIndex - 1); return; }
        var i = StatusGroups.IndexOf(StatusGroup);
        if (i > 0)
        {
            var prev = StatusGroups.Take(i).LastOrDefault(g => g.Items.Count > 0);
            if (prev != null) OpenStatus(prev);
        }
    }

    [RelayCommand]
    void CloseStatus()
    {
        ShowStatusViewer = false;
        ShowStatusViewers = false;
        ShowStatusShare = false;
        CurrentStatus = null;
        _ = LoadStatusesAsync();
    }

    [RelayCommand]
    async Task ReactStatus(string? emoji)
    {
        if (CurrentStatus is null || string.IsNullOrEmpty(emoji)) return;
        try
        {
            await _n.ReactToStatusAsync(CurrentStatus.Id, emoji);
            CurrentStatus.MyReaction = emoji;
            var chip = CurrentStatus.Reactions.FirstOrDefault(r => r.Emoji == emoji);
            if (chip is null) CurrentStatus.Reactions.Add(new ReactionChip { Emoji = emoji, Count = 1, Mine = true });
            else chip.Count++;
        }
        catch { }
    }

    [RelayCommand]
    async Task SendStatusReply()
    {
        if (CurrentStatus is null || StatusGroup is null) return;
        var text = StatusReply.Trim();
        if (text.Length == 0 || StatusGroup.IsMine) return;
        try
        {
            var row = await _n.SendAsync(StatusGroup.PeerId, new MsgDraft
            {
                Text = text,
                ReplyUid = CurrentStatus.Id,
                ReplyAuthor = StatusGroup.Name,
                ReplyPreview = CurrentStatus.Caption.Length > 0 ? CurrentStatus.Caption : "Statut"
            });
            if (row is null) return;
            _n.Deliver(StatusGroup.PeerId);
            StatusReply = "";
            Toast("Réponse envoyée", StatusGroup.Name);
            var peer = StatusGroup.PeerId;
            ShowStatusViewer = false;
            await ReloadAsync();
            await LoadStatusesAsync();
        }
        catch { }
    }

    [RelayCommand]
    void ToggleStatusViewers()
    {
        ShowStatusShare = false;
        ShowStatusViewers = !ShowStatusViewers;
        if (!ShowStatusViewers || CurrentStatus is null) return;
        StatusViewerList.Clear();
        foreach (var v in CurrentStatus.Viewers)
            StatusViewerList.Add(new StatusViewer(string.IsNullOrEmpty(v.ViewerName) ? "Contact" : v.ViewerName,
                                                  Fmt.Seen(v.At), string.IsNullOrEmpty(v.ViewerName) ? "?" : v.ViewerName));
    }

    [RelayCommand]
    void ToggleStatusShare()
    {
        ShowStatusViewers = false;
        ShowStatusShare = !ShowStatusShare;
        if (!ShowStatusShare) return;
        StatusShareTargets.Clear();
        foreach (var c in Conversations.Where(c => !c.IsGroup || true))
            StatusShareTargets.Add(c);
    }

    [RelayCommand]
    async Task ShareStatusTo(ConversationItem? target)
    {
        if (target is null || CurrentStatus is null) return;
        var s = CurrentStatus;
        try
        {
            MsgDraft draft;
            if (s.IsText)
            {
                draft = new MsgDraft { Text = s.Text, Forwarded = true };
            }
            else
            {
                if (!MediaStore.Exists(s.MediaId))
                {
                    Toast("Partager", "Le média n'est pas encore sur cet ordinateur.");
                    return;
                }
                draft = new MsgDraft
                {
                    Kind = s.Kind, MediaId = s.MediaId, MediaName = s.MediaName, MediaSize = s.MediaSize,
                    MediaMime = MediaStore.MimeOf(s.MediaId), Text = s.Text, Forwarded = true
                };
            }
            var row = await _n.SendAsync(target.PeerId, draft);
            if (row is null) return;
            _n.Deliver(target.PeerId);
            ShowStatusShare = false;
            Toast("Statut partagé", target.Name);
            await ReloadAsync();
        }
        catch { }
    }

    [RelayCommand]
    async Task DeleteCurrentStatus()
    {
        if (CurrentStatus is null) return;
        await _n.DeleteStatusAsync(CurrentStatus.Id);
        ShowStatusViewer = false;
        await LoadStatusesAsync();
        Toast("Statut supprimé", "Il a été retiré pour tout le monde.");
    }

    [RelayCommand]
    async Task OpenStatusMedia()
    {
        if (CurrentStatus is null) return;
        if (!MediaStore.Exists(CurrentStatus.MediaId))
        {
            await _n.RetryStatusMediaAsync(CurrentStatus.Id);
            await RefreshStatusMediaAsync(CurrentStatus.Id);
        }
        if (CurrentStatus.MediaPath is { } path && File.Exists(path))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { }
        }
    }

    // =====================================================================
    //  Composeur
    // =====================================================================
    [RelayCommand]
    void OpenStatusComposer()
    {
        ResetStatusComposer();
        StatusAudiencePicks.Clear();
        foreach (var c in _known.Values.Where(c => !c.IsGroup))
            StatusAudiencePicks.Add(new ContactChoice { PeerId = c.PeerId, Name = c.Name, Photo = c.Photo });
        ShowStatusComposer = true;
    }

    [RelayCommand]
    void CloseStatusComposer() => ShowStatusComposer = false;

    void ResetStatusComposer()
    {
        StatusText = "";
        StatusKind = Core.Kind.Text;
        StatusSourcePath = null;
        StatusPhoto = null;
        StatusMediaLabel = "";
        StatusHasMedia = false;
        StatusColor = 0;
        StatusAudienceOnly = false;
        foreach (var o in StatusColorOptions) o.IsSelected = o.Index == 0;
    }

    [RelayCommand] async Task AddStatusImage() => await PickStatusMedia(PickKind.Image);
    [RelayCommand] async Task AddStatusVideo() => await PickStatusMedia(PickKind.Video);

    Task PickStatusMedia(PickKind kind)
    {
        var path = PickFile?.Invoke(kind);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Task.CompletedTask;
        StatusSourcePath = path;
        StatusMediaLabel = Path.GetFileName(path);
        StatusHasMedia = true;
        StatusKind = kind == PickKind.Image ? Core.Kind.Image : Core.Kind.Video;
        StatusPhoto = null;
        if (kind == PickKind.Image)
        {
            try { StatusPhoto = Img.FromFile(path); } catch { }
        }
        return Task.CompletedTask;
    }

    [RelayCommand]
    void ClearStatusMedia()
    {
        StatusSourcePath = null;
        StatusPhoto = null;
        StatusMediaLabel = "";
        StatusHasMedia = false;
        StatusKind = Core.Kind.Text;
    }

    [RelayCommand]
    void InsertStatusEmoji(string? emoji)
    {
        if (!string.IsNullOrEmpty(emoji)) StatusText += emoji;
    }

    [RelayCommand]
    void PickStatusColor(int index)
    {
        StatusColor = index;
        foreach (var o in StatusColorOptions) o.IsSelected = o.Index == index;
    }

    [RelayCommand]
    void ChooseStatusAudience(string? mode) => StatusAudienceOnly = mode == "only";

    [RelayCommand]
    async Task PublishStatus()
    {
        if (StatusPublishing) return;
        var text = StatusText.Trim();
        if (text.Length == 0 && StatusSourcePath is null)
        {
            Toast("Statut", "Écrivez un texte ou choisissez une image / une vidéo.");
            return;
        }
        var audience = StatusAudienceOnly
            ? StatusAudiencePicks.Where(p => p.IsSelected).Select(p => p.PeerId).ToList()
            : new List<string>();
        if (StatusAudienceOnly && audience.Count == 0)
        {
            Toast("Statut", "Choisissez au moins une personne.");
            return;
        }

        StatusPublishing = true;
        try
        {
            var kind = Core.Kind.Text;
            string mediaId = "", mediaName = "", mime = "";
            long size = 0;
            if (StatusSourcePath is { } path && File.Exists(path))
            {
                kind = MediaStore.KindOf(path);
                if (kind != Core.Kind.Image && kind != Core.Kind.Video) kind = StatusKind;
                var (id, sz) = MediaStore.Import(path);
                mediaId = id; size = sz;
                mediaName = Path.GetFileName(path);
                mime = MediaStore.MimeOf(path);
            }
            await _n.PublishStatusAsync(new StatusDraft
            {
                Kind = kind, Text = text, MediaId = mediaId, MediaName = mediaName, MediaMime = mime, MediaSize = size,
                Background = StatusColor.ToString(),
                Audience = StatusAudienceOnly ? "only" : "contacts",
                AudienceList = audience
            });
            ShowStatusComposer = false;
            ResetStatusComposer();
            await LoadStatusesAsync();
            Toast("Statut publié", "Visible par vos contacts pendant 24 heures.");
        }
        catch (Exception ex) { Toast("Statut", ex.Message); }
        finally { StatusPublishing = false; }
    }
}

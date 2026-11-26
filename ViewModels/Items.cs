using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using ChatLan.Core;
using ChatLan.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;


namespace ChatLan.ViewModels;

// ---------- Une ligne de la liste (un contact / un groupe / une discussion) ----------
public partial class ConversationItem : ObservableObject
{
    int _typingTick;

    public ContactRow Contact { get; private set; }
    public string PeerId => Contact.PeerId;
    public string Name => Contact.DisplayName;
    public string About => Contact.About;
    public string Username => Contact.Username;
    public bool IsGroup => Contact.IsGroup;
    public string LastUid { get; private set; } = "";
    public bool HasMessages { get; private set; }
    public DateTime LastAt { get; private set; }
    public int PhotoVersion { get; set; } = -1;

    // Actions fournies par l'écran principal
    public Action<ConversationItem>? OpenRequested { get; set; }
    public Action<ConversationItem>? DeleteRequested { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(PreviewText))] string _preview = "";
    [ObservableProperty] string _time = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUnread))] int _unread;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))] bool _online;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))] DateTime? _lastSeen;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PreviewText)), NotifyPropertyChangedFor(nameof(StatusText))] bool _typing;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowTick))] bool _mine;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowTick))] bool _deleted;
    [ObservableProperty] int _status;
    [ObservableProperty] bool _isSelected;
    [ObservableProperty] ImageSource? _photo;

    public bool HasUnread => Unread > 0;
    public bool ShowTick => HasMessages && Mine && !Deleted && !IsGroup;
    public string PreviewText => Typing ? "écrit..." : HasMessages ? Preview : About;
    public string StatusText => Typing ? "écrit..." : IsGroup ? About : Online ? "en ligne" : Fmt.Seen(LastSeen);
    public bool ShowOnlineDot => !IsGroup && Online;

    public ConversationItem(ContactRow c, bool online)
    {
        Contact = c; _online = online;
        _lastSeen = c.LastSeen is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;
    }

    public void Update(ContactRow c)
    {
        Contact = c;
        if (!Online && c.LastSeen is { } d) LastSeen = DateTime.SpecifyKind(d, DateTimeKind.Utc);
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(About));
        OnPropertyChanged(nameof(Username));
        OnPropertyChanged(nameof(IsGroup));
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ShowTick));
        OnPropertyChanged(nameof(ShowOnlineDot));
    }

    public void SetLast(MessageRow m)
    {
        LastUid = m.Uid; HasMessages = true;
        var l = Fmt.Local(m.CreatedAt); LastAt = l; Time = Fmt.ListTime(l);
        Mine = m.FromMe; Status = m.Status; Deleted = m.Deleted;
        Preview = MsgPreview.Of(m);
        OnPropertyChanged(nameof(ShowTick));
        OnPropertyChanged(nameof(PreviewText));
    }

    public void ClearConversation()
    {
        HasMessages = false; LastUid = ""; Preview = ""; Time = ""; Unread = 0; Mine = false; Deleted = false;
        OnPropertyChanged(nameof(ShowTick));
        OnPropertyChanged(nameof(PreviewText));
    }

    public void MarkDeleted() { Deleted = true; Preview = MsgPreview.Of(new MessageRow { Deleted = true, FromMe = Mine }); }

    public async void PulseTyping()
    {
        Typing = true;
        var t = ++_typingTick;
        await Task.Delay(3000);
        if (t == _typingTick) Typing = false;
    }

    [RelayCommand] void Open() => OpenRequested?.Invoke(this);
    [RelayCommand] void DeleteChat() => DeleteRequested?.Invoke(this);
}

// ---------- Une puce de réaction sous une bulle ----------
public partial class ReactionChip : ObservableObject
{
    public string Emoji { get; init; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CountText))] int _count;
    [ObservableProperty] bool _mine;
    public string CountText => Count > 1 ? Count.ToString() : "";
}

// ---------- Actions déléguées par l'écran principal ----------
public sealed class MessageActions
{
    public Func<MessageItem, Task> DeleteForMe { get; init; } = _ => Task.CompletedTask;
    public Func<MessageItem, Task> DeleteForAll { get; init; } = _ => Task.CompletedTask;
    public Action<MessageItem>? Reply { get; init; }
    public Action<MessageItem>? Edit { get; init; }
    public Action<MessageItem>? Forward { get; init; }
    public Func<MessageItem, string, Task>? React { get; init; }
    public Action<MessageItem>? OpenMedia { get; init; }
    public Func<MessageItem, Task>? Retry { get; init; }
}

// ---------- Un message dans la conversation ----------
public partial class MessageItem : ObservableObject
{
    readonly MessageActions _actions;

    public int Id { get; }
    public string Uid { get; }
    public bool IsMine { get; }
    public DateTime At { get; }
    public string Time => At.ToString("HH:mm");

    // --- nature du message ---
    public string Kind { get; }
    public string MediaId { get; }
    public string MediaName { get; }
    public string MediaMime { get; }
    public string ContactId { get; }
    public string SenderName { get; }
    public bool ShowSender { get; }
    public bool Forwarded { get; }
    public string ReplyAuthor { get; }
    public string ReplyPreview { get; }
    public bool HasReply => ReplyPreview.Length > 0 || ReplyAuthor.Length > 0;
    public int DurationMs { get; }

    public bool HasMedia => Core.Kind.IsMedia(Kind);
    public bool IsSticker => Kind == Core.Kind.Sticker;
    public bool IsImage => Kind == Core.Kind.Image;
    public bool IsGifImage => IsImage && MediaStore.IsGif(MediaId);
    public bool IsStaticImage => IsImage && !IsGifImage;
    public bool IsVideo => Kind == Core.Kind.Video;
    public bool IsVoice => Kind == Core.Kind.Voice || Kind == Core.Kind.Audio;
    public bool IsFile => Kind == Core.Kind.File;
    public bool HasText => !Deleted && Text.Length > 0;
    public bool HasCaption => HasText && HasMedia && !IsVoice;

    public bool IsEdited => EditedAt != null;
    public string EditedText => IsEdited ? "modifié" : "";
    public string ForwardedText => Forwarded ? "↪ Transféré" : "";
    public string DurationText => MediaStore.DurationText(DurationMs);
    public string SizeText => MediaStore.SizeText(MediaSize);
    public string FileSubtitle => SizeText;
    public string SenderInitials => SenderName.Length > 0 ? SenderName : "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Display))] string _text;
    [ObservableProperty,
     NotifyPropertyChangedFor(nameof(Display)),
     NotifyCanExecuteChangedFor(nameof(DeleteForAllCommand)),
     NotifyCanExecuteChangedFor(nameof(CopyCommand)),
     NotifyCanExecuteChangedFor(nameof(EditCommand))] bool _deleted;
    [ObservableProperty] int _status;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasHeader))] string? _dateHeader;
    [ObservableProperty,
     NotifyPropertyChangedFor(nameof(Radius)),
     NotifyPropertyChangedFor(nameof(Spacing))] bool _firstInRun;

    // --- niveau 2 ---
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEdited), nameof(EditedText))] DateTime? _editedAt;
    [ObservableProperty] ImageSource? _mediaSource;
    [ObservableProperty] string? _mediaPath;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanPlay))] bool _mediaMissing;
    [ObservableProperty] bool _isPlaying;
    [ObservableProperty] double _playProgress;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasReactions))] bool _reactionsReady;

    public ObservableCollection<ReactionChip> Reactions { get; } = new();
    public bool HasReactions => Reactions.Count > 0;
    public bool CanPlay => !MediaMissing && MediaPath != null;
    public long MediaSize { get; }

    public bool HasHeader => DateHeader != null;

    public CornerRadius Radius
    {
        get
        {
            if (!FirstInRun) return new CornerRadius(18);
            return IsMine ? new CornerRadius(18, 6, 18, 18) : new CornerRadius(6, 18, 18, 18);
        }
    }

    public Thickness Spacing => FirstInRun ? new Thickness(0, 8, 0, 2) : new Thickness(0, 2, 0, 2);
    public HorizontalAlignment Align => IsMine ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    // Espaces insécables : réservent la place de l'heure et des coches dans la bulle.
    // Un média sans légende n'ajoute pas de ligne vide.
    public string Display
    {
        get
        {
            if (Deleted) return (IsMine ? "Vous avez supprimé ce message" : "Ce message a été supprimé")
                                  + new string('\u00A0', IsMine ? 14 : 9);
            if (IsSticker) return Text;
            return Text.Length == 0 ? "" : Text + new string('\u00A0', IsMine ? 14 : 9);
        }
    }

    public MessageItem(MessageRow m, MessageActions actions, bool groupConversation = false)
    {
        Id = m.Id; Uid = m.Uid; IsMine = m.FromMe; At = Fmt.Local(m.SentAt);
        _text = m.Text; _deleted = m.Deleted; _status = m.Status;
        _editedAt = m.EditedAt;
        Kind = string.IsNullOrEmpty(m.Kind) ? Core.Kind.Text : m.Kind;
        MediaId = m.MediaId ?? "";
        MediaName = m.MediaName ?? "";
        MediaMime = m.MediaMime ?? "";
        ContactId = m.ContactId ?? "";
        MediaSize = m.MediaSize;
        DurationMs = m.DurationMs;
        SenderName = m.FromName ?? "";
        ShowSender = groupConversation && !m.FromMe && SenderName.Length > 0;
        Forwarded = m.Forwarded;
        ReplyAuthor = m.ReplyAuthor ?? "";
        ReplyPreview = m.ReplyPreview ?? "";
        _actions = actions;
        RefreshMedia();
    }

    // Charge l'image locale si la pièce jointe est déjà sur ce PC
    public void RefreshMedia()
    {
        var path = MediaStore.Exists(MediaId) ? MediaStore.PathOf(MediaId) : null;
        MediaPath = path;
        MediaMissing = HasMedia && !IsVoice && path is null;
        if (path != null && IsStaticImage)
        {
            try { MediaSource = Img.FromFile(path); } catch { }
        }
    }

    public void MarkDeleted() { Deleted = true; Text = ""; }

    public void ApplyEdit(string text, DateTime? editedAt)
    {
        Text = text;
        EditedAt = editedAt ?? DateTime.UtcNow;
    }

    public void SetReactions(IEnumerable<ReactionRow> rows, string myPeerId)
    {
        Reactions.Clear();
        foreach (var g in rows.GroupBy(r => r.Emoji))
            Reactions.Add(new ReactionChip
            {
                Emoji = g.Key,
                Count = g.Count(),
                Mine = g.Any(r => r.AuthorPeerId == myPeerId)
            });
        OnPropertyChanged(nameof(HasReactions));
        ReactionsReady = Reactions.Count > 0;
    }

    public void RefreshPlayback()
    {
        IsPlaying = VoicePlayer.IsPlaying(Uid);
        if (IsPlaying)
        {
            var total = DurationMs > 0 ? DurationMs : VoicePlayer.Total;
            PlayProgress = total > 0 ? Math.Clamp(VoicePlayer.Position / total, 0, 1) : 0;
        }
        else if (PlayProgress > 0 && !VoicePlayer.IsPlaying(Uid)) PlayProgress = 0;
    }

    [RelayCommand] Task DeleteForMe() => _actions.DeleteForMe(this);
    [RelayCommand(CanExecute = nameof(CanDeleteForAll))] Task DeleteForAll() => _actions.DeleteForAll(this);
    bool CanDeleteForAll() => IsMine && !Deleted;
    [RelayCommand(CanExecute = nameof(CanCopy))] void Copy() { try { Clipboard.SetText(Text); } catch { } }
    bool CanCopy() => !Deleted && !string.IsNullOrEmpty(Text);
    [RelayCommand(CanExecute = nameof(CanEdit))] void Edit() => _actions.Edit?.Invoke(this);
    bool CanEdit() => IsMine && !Deleted && !IsVoice && Kind != Core.Kind.Sticker;
    [RelayCommand] void Reply() => _actions.Reply?.Invoke(this);
    [RelayCommand] void Forward() => _actions.Forward?.Invoke(this);
    [RelayCommand] async Task React(string? emoji)
    {
        if (_actions.React != null && !string.IsNullOrEmpty(emoji)) await _actions.React(this, emoji);
    }
    [RelayCommand] void OpenMedia() => _actions.OpenMedia?.Invoke(this);
    [RelayCommand] async Task Retry() { if (_actions.Retry != null) await _actions.Retry(this); }

    [RelayCommand]
    void PlayVoice()
    {
        if (MediaPath is null || !File.Exists(MediaPath)) return;
        VoicePlayer.Toggle(Uid, MediaPath);
        RefreshPlayback();
    }
}

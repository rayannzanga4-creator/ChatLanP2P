using ChatLan.Core;
using ChatLan.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatLan.ViewModels;

// ---------- Navigation : Accueil -> Connexion/Inscription -> Messagerie ----------
public partial class MainViewModel : ObservableObject
{
    PeerNode? _node;

    [ObservableProperty] object? _current;
    [ObservableProperty] string _title = "ChatLan";

    public MainViewModel() { ShowWelcome(); }

    void ShowWelcome()
    {
        Title = "ChatLan";
        Current = new WelcomeViewModel(ShowAuth);
    }

    void ShowAuth(bool register)
    {
        Title = "ChatLan";
        Current = new AuthViewModel(OnAuthenticated, ShowWelcome) { IsRegister = register };
    }

    async void OnAuthenticated(PeerNode node)
    {
        _node = node;
        var shell = new ShellViewModel(node, LogoutAsync);
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.UnreadTotal))
                Title = shell.UnreadTotal > 0 ? $"({shell.UnreadTotal}) ChatLan" : "ChatLan";
        };
        Current = shell;
        await shell.InitAsync();
    }

    async Task LogoutAsync()
    {
        var node = _node;
        _node = null;
        if (node != null) await Task.Run(() => node.StopAsync());
        ShowAuth(false);
    }

    // Appelé à la fermeture de la fenêtre : prévient les autres PC qu'on part
    public void Shutdown()
    {
        var node = _node;
        _node = null;
        if (node == null) return;
        try { Task.Run(() => node.StopAsync()).Wait(3000); } catch { }
    }
}

// ---------- Page d'accueil ----------
public partial class WelcomeViewModel : ObservableObject
{
    readonly Action<bool> _go;
    public WelcomeViewModel(Action<bool> go) { _go = go; }

    [RelayCommand] void Login() => _go(false);
    [RelayCommand] void Register() => _go(true);
    [RelayCommand] void ToggleTheme() => ThemeManager.Toggle();
}

// ---------- Connexion / Inscription (comptes locaux) ----------
public partial class AuthViewModel : ObservableObject
{
    readonly Action<PeerNode> _success;
    readonly Action _back;

    public AuthViewModel(Action<PeerNode> success, Action back)
    {
        _success = success; _back = back;
        _username = Prefs.Current.LastUsername;
    }

    [ObservableProperty] string _username;
    [ObservableProperty] string _displayName = "";
    [ObservableProperty] bool _isRegister;
    [ObservableProperty] string _error = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SubmitCommand))] bool _busy;

    // Renseignés par la vue (PasswordBox ne supporte pas la liaison directe)
    public string Password { get; set; } = "";
    public string Confirm { get; set; } = "";

    [RelayCommand] void GoBack() => _back();
    [RelayCommand] void ToggleTheme() => ThemeManager.Toggle();

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    async Task Submit()
    {
        Error = "";
        var un = Username.Trim();
        if (un.Length == 0) { Error = "Saisissez votre nom d'utilisateur."; return; }
        if (Password.Length == 0) { Error = "Saisissez votre mot de passe."; return; }
        if (IsRegister)
        {
            if (DisplayName.Trim().Length < 2) { Error = "Saisissez votre nom affiché (2 caractères minimum)."; return; }
            if (Password != Confirm) { Error = "Les deux mots de passe ne correspondent pas."; return; }
        }

        Busy = true;
        try
        {
            var outcome = IsRegister
                ? await Accounts.RegisterAsync(un, Password, DisplayName)
                : await Accounts.LoginAsync(un, Password);
            if (outcome.Error != null) { Error = outcome.Error; return; }

            PeerNode node;
            try { node = await PeerNode.StartAsync(outcome.DbPath!, outcome.Profile!); }
            catch (Exception ex) { Error = "Impossible de démarrer le réseau local : " + ex.Message; return; }

            Prefs.Current.LastUsername = un.ToLowerInvariant();
            Prefs.Save();
            _success(node);
        }
        finally { Busy = false; }
    }

    bool CanSubmit() => !Busy;
}

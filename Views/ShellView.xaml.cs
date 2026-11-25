using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ChatLan.ViewModels;
using Microsoft.Win32;

namespace ChatLan.Views;

public partial class ShellView : UserControl
{
    public ShellView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SizeChanged += (_, _) => ApplyLayout();
    }

    ShellViewModel? Vm => DataContext as ShellViewModel;

    void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not ShellViewModel vm) return;

        // Boîtes de dialogue de fichiers demandées par le modèle de vue
        vm.PickFile = Pick;

        // Défilement automatique vers le dernier message ajouté
        vm.Messages.CollectionChanged += (_, a) =>
        {
            if (a.Action != NotifyCollectionChangedAction.Add) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (MsgList.Items.Count > 0) MsgList.ScrollIntoView(MsgList.Items[MsgList.Items.Count - 1]);
            }), DispatcherPriority.Background);
        };

        // Après le chargement des anciens messages : rester sur le même message
        vm.RequestScroll += item =>
            Dispatcher.BeginInvoke(new Action(() => MsgList.ScrollIntoView(item)), DispatcherPriority.Background);

        // Répondre ou modifier : le curseur revient dans la zone de saisie
        vm.RequestFocusComposer += () =>
            Dispatcher.BeginInvoke(new Action(() => Input.Focus()), DispatcherPriority.Input);

        vm.PropertyChanged += (_, a) =>
        {
            if (a.PropertyName != nameof(ShellViewModel.Selected) && a.PropertyName != nameof(ShellViewModel.ShowProfile)) return;
            ApplyLayout();
            if (a.PropertyName == nameof(ShellViewModel.Selected))
                Dispatcher.BeginInvoke(new Action(() => Input.Focus()), DispatcherPriority.Input);
        };
        ApplyLayout();
    }

    static string? Pick(PickKind kind)
    {
        var dlg = new OpenFileDialog { Title = "Choisir un fichier" };
        switch (kind)
        {
            case PickKind.Image:
                dlg.Filter = "Images et GIF|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|Toutes les images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp";
                break;
            case PickKind.Video:
                dlg.Filter = "Vidéos|*.mp4;*.mov;*.avi;*.mkv;*.webm;*.wmv";
                break;
            default:
                dlg.Filter = "Documents|*.pdf;*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx;*.txt;*.zip;*.rar|Tous les fichiers|*.*";
                break;
        }
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    // Mode "mobile" : sous 800 px, soit la liste, soit la conversation
    void ApplyLayout()
    {
        if (Vm is not { } vm) return;
        vm.IsNarrow = ActualWidth > 0 && ActualWidth < 800;
        if (!vm.IsNarrow)
        {
            ColRail.Width = new GridLength(76);
            ColList.Width = new GridLength(Math.Clamp(ActualWidth * 0.30, 350, 440));
            ColChat.Width = new GridLength(1, GridUnitType.Star);
        }
        else if (vm.Selected == null)
        {
            ColRail.Width = new GridLength(76);
            ColList.Width = new GridLength(1, GridUnitType.Star);
            ColChat.Width = new GridLength(0);
        }
        else
        {
            ColRail.Width = new GridLength(0);
            ColList.Width = new GridLength(0);
            ColChat.Width = new GridLength(1, GridUnitType.Star);
        }
    }

    // Entrée = envoyer, Maj+Entrée = nouvelle ligne
    void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            Vm?.SendCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            Vm?.CancelReplyCommand.Execute(null);
            Vm?.CancelEditCommand.Execute(null);
        }
    }

    void Emoji_Click(object sender, RoutedEventArgs e) => EmojiPopup.IsOpen = !EmojiPopup.IsOpen;

    void Attach_Click(object sender, RoutedEventArgs e)
    {
        if (AttachBtn.ContextMenu is not { } menu) return;
        menu.PlacementTarget = AttachBtn;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    void New_Click(object sender, RoutedEventArgs e)
    {
        if (NewBtn.ContextMenu is not { } menu) return;
        menu.PlacementTarget = NewBtn;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void More_Click(object sender, RoutedEventArgs e)
    {
        if (MoreBtn.ContextMenu is not { } menu) return;
        menu.PlacementTarget = MoreBtn;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // Glisser-déposer d'un fichier dans la conversation
    void Chat_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    async void Chat_Drop(object sender, DragEventArgs e)
    {
        if (Vm is not { } vm || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        foreach (var path in files.Take(3))
            if (File.Exists(path)) await vm.SendMediaAsync(path);
    }

    async void ChangePhoto_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
            Title = "Choisir une photo de profil"
        };
        if (dlg.ShowDialog() == true && Vm is { } vm)
            await vm.ChangePhotoAsync(dlg.FileName);
    }
}

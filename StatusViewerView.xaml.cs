using System.Windows.Controls;
using System.Windows.Input;
using ChatLan.ViewModels;

namespace ChatLan.Views;

public partial class StatusViewerView : UserControl
{
    public StatusViewerView() => InitializeComponent();

    void Reply_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (DataContext is ShellViewModel vm && vm.SendStatusReplyCommand.CanExecute(null))
            vm.SendStatusReplyCommand.Execute(null);
    }
}

using System.Windows;
using System.Windows.Controls;
using ChatLan.ViewModels;

namespace ChatLan.Views;

public partial class AuthView : UserControl
{
    public AuthView()
    {
        InitializeComponent();
        PasswordBox.PasswordChanged += (_, _) => { if (DataContext is AuthViewModel vm) vm.Password = PasswordBox.Password; };
        ConfirmBox.PasswordChanged += (_, _) => { if (DataContext is AuthViewModel vm) vm.Confirm = ConfirmBox.Password; };
        Loaded += (_, _) => UsernameBox.Focus();
    }
}

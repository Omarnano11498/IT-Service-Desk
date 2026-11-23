using System.Windows;
using System.Windows.Controls;
using ServiceDesk.Wpf.ViewModels;

namespace ServiceDesk.Wpf.Views;

/// <summary>PasswordBox.Password is not a dependency property (by design, to keep secrets out of bindings);
/// this thin code-behind forwards it to the ViewModel — no business logic here.</summary>
public partial class AdminLoginView : UserControl
{
    public AdminLoginView()
    {
        InitializeComponent();
        Loaded += (_, _) => UserNameBox.Focus();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is AdminLoginViewModel vm) vm.Password = PasswordBox.Password;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is AdminLoginViewModel oldVm) oldVm.PasswordResetRequested -= OnReset;
        if (e.NewValue is AdminLoginViewModel newVm) newVm.PasswordResetRequested += OnReset;
    }

    private void OnReset(object? sender, EventArgs e) => PasswordBox.Clear();
}

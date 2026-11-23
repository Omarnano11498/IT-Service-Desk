using System.Windows;
using System.Windows.Controls;
using ServiceDesk.Wpf.ViewModels;

namespace ServiceDesk.Wpf.Views;

/// <summary>Forwards PasswordBox values to the ViewModel (PasswordBox.Password is deliberately not bindable).</summary>
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SettingsViewModel oldVm) oldVm.PasswordFieldsResetRequested -= OnPasswordReset;
        if (e.NewValue is SettingsViewModel vm)
        {
            vm.PasswordFieldsResetRequested += OnPasswordReset;
            SqlPasswordBox.Password = vm.SqlPassword;   // pre-fill the decrypted saved SQL password
        }
    }

    private void OnSqlPasswordChanged(object sender, RoutedEventArgs e) => Vm?.OnSqlPasswordChanged(SqlPasswordBox.Password);

    private void OnAdminPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.CurrentPassword = CurrentPasswordBox.Password;
        vm.NewPassword = NewPasswordBox.Password;
        vm.ConfirmPassword = ConfirmPasswordBox.Password;
    }

    private void OnPasswordReset(object? sender, EventArgs e)
    {
        CurrentPasswordBox.Clear();
        NewPasswordBox.Clear();
        ConfirmPasswordBox.Clear();
    }
}

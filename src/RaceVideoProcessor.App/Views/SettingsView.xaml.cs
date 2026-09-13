using System.Windows;
using System.Windows.Controls;
using RaceVideoProcessor.App.ViewModels;

namespace RaceVideoProcessor.App.Views;

public partial class SettingsView : UserControl
{
    private bool _loadingPassword;

    public SettingsView()
    {
        InitializeComponent();

        // PasswordBox deliberately offers no bindable property, so the password is
        // never exposed through the binding system; it is copied across here instead.
        DataContextChanged += (_, _) => LoadPassword();
        Loaded += (_, _) => LoadPassword();
    }

    private void LoadPassword()
    {
        if (DataContext is not MainViewModel vm || PasswordInput.Password == vm.Settings.LoginPassword)
            return;
        _loadingPassword = true;
        PasswordInput.Password = vm.Settings.LoginPassword;
        _loadingPassword = false;
    }

    private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_loadingPassword && DataContext is MainViewModel vm)
            vm.Settings.LoginPassword = PasswordInput.Password;
    }
}

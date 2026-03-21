using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using NVMeDriverPatcher.ViewModels;

namespace NVMeDriverPatcher.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        _vm.ConfirmDialog = ShowConfirmDialog;
        _vm.InfoDialog = ShowInfoDialog;
        DataContext = _vm;

        // Set window icon from embedded resource (pack URI fails in single-file publish)
        try
        {
            var iconUri = new Uri("pack://application:,,,/nvme.ico", UriKind.Absolute);
            Icon = BitmapFrame.Create(iconUri);
        }
        catch { /* Icon load best-effort */ }

        var workArea = SystemParameters.WorkArea;
        if (Height > workArea.Height) Height = workArea.Height - 20;
        if (Width > workArea.Width) Width = workArea.Width - 20;

        ContentRendered += async (_, _) => await _vm.RunPreflightAsync();
        Closing += (_, _) => _vm.OnClosing();
    }

    private bool ShowConfirmDialog(string title, string message)
    {
        return ThemedDialog.Show(message, title, DialogButtons.YesNo, DialogIcon.Question, this) == "Yes";
    }

    private void ShowInfoDialog(string title, string message, DialogIcon icon)
    {
        ThemedDialog.Show(message, title, DialogButtons.OK, icon, this);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateBadge_Click(object sender, MouseButtonEventArgs e) => _vm.OpenUpdateUrlCommand.Execute(null);
    private void GitHub_Click(object sender, RoutedEventArgs e) => _vm.OpenGitHubCommand.Execute(null);
    private void Docs_Click(object sender, RoutedEventArgs e) => _vm.OpenDocsCommand.Execute(null);

    private void SettingsToggle_Click(object sender, MouseButtonEventArgs e) => _vm.ToggleSettingsCommand.Execute(null);

    private void LogOutput_TextChanged(object sender, TextChangedEventArgs e)
    {
        LogScroller?.ScrollToBottom();
    }

    private void CopySelection_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(LogOutput.SelectedText))
            Clipboard.SetText(LogOutput.SelectedText);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        LogOutput.SelectAll();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _vm.ClearLog();
    }
}

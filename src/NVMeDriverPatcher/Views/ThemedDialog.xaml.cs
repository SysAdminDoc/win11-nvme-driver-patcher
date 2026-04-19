using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WpfPath = System.Windows.Shapes.Path;

namespace NVMeDriverPatcher.Views;

public enum DialogIcon { Information, Warning, Error, Question }
public enum DialogButtons { OK, YesNo }

public partial class ThemedDialog : Window
{
    public string Result { get; private set; } = "No";

    private ThemedDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => FocusPrimaryAction();
        // Esc closes the dialog. For OK-only, Esc returns "OK" (acknowledged). For Yes/No,
        // Esc returns "No" via the BtnNo IsCancel binding. Backstop in case both are hidden.
        PreviewKeyDown += (_, ev) =>
        {
            if (ev.Key == System.Windows.Input.Key.Escape)
            {
                if (BtnNo.Visibility == Visibility.Visible) Result = "No";
                else if (BtnOK.Visibility == Visibility.Visible) Result = "OK";
                Close();
                ev.Handled = true;
            }
        };
    }

    public static string Show(string message, string title = "NVMe Driver Patcher",
        DialogButtons buttons = DialogButtons.OK, DialogIcon icon = DialogIcon.Information,
        Window? owner = null)
    {
        var dlg = new ThemedDialog();
        dlg.DlgTitle.Text = string.IsNullOrWhiteSpace(title) ? "NVMe Driver Patcher" : title;
        dlg.DlgMessage.Text = message ?? string.Empty;

        if (buttons == DialogButtons.YesNo)
        {
            dlg.BtnYes.Visibility = Visibility.Visible;
            dlg.BtnNo.Visibility = Visibility.Visible;
            var labels = ResolveButtonLabels(title);
            dlg.BtnYes.Content = labels.affirmative;
            dlg.BtnNo.Content = labels.dismissive;
            dlg.Result = "No";
        }
        else
        {
            dlg.BtnOK.Visibility = Visibility.Visible;
            dlg.Result = "OK";
        }

        // Draw icon
        var iconColor = icon switch
        {
            DialogIcon.Error => "#FFef4444",
            DialogIcon.Warning => "#FFf59e0b",
            DialogIcon.Question => "#FF3b82f6",
            _ => "#FF3b82f6"
        };
        var actionSurface = icon switch
        {
            DialogIcon.Error => "#FF3E1A1F",
            DialogIcon.Warning => "#FF4A3516",
            DialogIcon.Question => "#FF133256",
            _ => "#FF133256"
        };
        var bc = new BrushConverter();
        // Defensive: BrushConverter.ConvertFromString can throw on a malformed color string.
        // The literals here are static, but keep this resilient against future refactors.
        System.Windows.Media.Brush iconBrush;
        System.Windows.Media.Brush surfaceBrush;
        try { iconBrush = (System.Windows.Media.Brush)bc.ConvertFromString(iconColor)!; }
        catch { iconBrush = System.Windows.Media.Brushes.DodgerBlue; }
        try { surfaceBrush = (System.Windows.Media.Brush)bc.ConvertFromString(actionSurface)!; }
        catch { surfaceBrush = System.Windows.Media.Brushes.Black; }
        dlg.DlgEyebrow.Text = ResolveEyebrow(icon, title);
        dlg.DlgEyebrow.Foreground = iconBrush;
        dlg.HeaderAccentBar.Background = iconBrush;
        dlg.BtnYes.Background = surfaceBrush;
        dlg.BtnYes.BorderBrush = iconBrush;
        dlg.BtnYes.Foreground = ResolveResourceBrush(dlg, "TextPrimary", System.Windows.Media.Brushes.White);
        dlg.BtnOK.Background = surfaceBrush;
        dlg.BtnOK.BorderBrush = iconBrush;
        dlg.BtnOK.Foreground = ResolveResourceBrush(dlg, "TextPrimary", System.Windows.Media.Brushes.White);

        var ellipse = new Ellipse { Width = 28, Height = 28, Fill = iconBrush, Opacity = 0.15 };
        dlg.IconCanvas.Children.Add(ellipse);

        var iconPath = new WpfPath { Stroke = iconBrush, StrokeThickness = 2 };
        iconPath.Data = icon switch
        {
            DialogIcon.Error => Geometry.Parse("M 10,10 L 18,18 M 18,10 L 10,18"),
            DialogIcon.Warning => Geometry.Parse("M 14,8 L 14,15 M 14,18 L 14,18.5"),
            DialogIcon.Question => Geometry.Parse("M 11,10 C 11,7 17,7 17,10 C 17,12.5 14,12.5 14,15 M 14,18 L 14,18.5"),
            _ => Geometry.Parse("M 14,9 L 14,9.5 M 14,12 L 14,19")
        };
        if (icon is DialogIcon.Warning or DialogIcon.Information)
        {
            iconPath.StrokeThickness = 2.5;
            iconPath.StrokeStartLineCap = PenLineCap.Round;
            iconPath.StrokeEndLineCap = PenLineCap.Round;
        }
        if (icon == DialogIcon.Question)
        {
            iconPath.StrokeStartLineCap = PenLineCap.Round;
            iconPath.StrokeEndLineCap = PenLineCap.Round;
        }
        System.Windows.Controls.Canvas.SetLeft(iconPath, 0);
        System.Windows.Controls.Canvas.SetTop(iconPath, 0);
        dlg.IconCanvas.Children.Add(iconPath);

        if (owner is not null) dlg.Owner = owner;
        dlg.ShowDialog();
        return dlg.Result;
    }

    private static (string affirmative, string dismissive) ResolveButtonLabels(string title)
    {
        if (Contains(title, "inactive"))
            return ("Apply Fallback", "Not Now");
        if (Contains(title, "fallback applied") || Contains(title, "installation complete") || Contains(title, "removal complete"))
            return ("Restart", "Not Now");
        if (Contains(title, "apply patch"))
            return ("Apply Patch", "Cancel");
        if (Contains(title, "remove patch"))
            return ("Remove Patch", "Cancel");
        if (Contains(title, "clear activity log"))
            return ("Clear Log", "Cancel");
        if (Contains(title, "reset tuning"))
            return ("Reset", "Cancel");
        if (Contains(title, "apply tuning"))
            return ("Apply Tuning", "Cancel");

        return ("Continue", "Cancel");
    }

    private static string ResolveEyebrow(DialogIcon icon, string title)
    {
        if (icon == DialogIcon.Error)
            return "Stop";
        if (icon == DialogIcon.Warning)
        {
            if (Contains(title, "complete") || Contains(title, "restart") || Contains(title, "fallback applied"))
                return "Restart Prompt";
            if (Contains(title, "apply") || Contains(title, "remove") || Contains(title, "reset") || Contains(title, "clear"))
                return "Confirm Change";

            return "Warning";
        }

        return icon == DialogIcon.Question ? "Confirmation" : "Information";
    }

    private static bool Contains(string source, string value) =>
        source.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static Brush ResolveResourceBrush(FrameworkElement element, string key, Brush fallback)
    {
        return element.TryFindResource(key) as Brush ?? fallback;
    }

    private void BtnOK_Click(object sender, RoutedEventArgs e) { Result = "OK"; Close(); }
    private void BtnYes_Click(object sender, RoutedEventArgs e) { Result = "Yes"; Close(); }
    private void BtnNo_Click(object sender, RoutedEventArgs e) { Result = "No"; Close(); }
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); }
        catch (InvalidOperationException) { /* not in a draggable state */ }
    }

    private void FocusPrimaryAction()
    {
        if (BtnYes.Visibility == Visibility.Visible)
        {
            BtnYes.Focus();
            return;
        }

        if (BtnOK.Visibility == Visibility.Visible)
        {
            BtnOK.Focus();
            return;
        }

        if (BtnNo.Visibility == Visibility.Visible)
            BtnNo.Focus();
    }
}

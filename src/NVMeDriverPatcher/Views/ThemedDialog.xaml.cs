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
    }

    public static string Show(string message, string title = "NVMe Driver Patcher",
        DialogButtons buttons = DialogButtons.OK, DialogIcon icon = DialogIcon.Information,
        Window? owner = null)
    {
        var dlg = new ThemedDialog();
        dlg.DlgTitle.Text = title;
        dlg.DlgMessage.Text = message;

        if (buttons == DialogButtons.YesNo)
        {
            dlg.BtnYes.Visibility = Visibility.Visible;
            dlg.BtnNo.Visibility = Visibility.Visible;
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
        var bc = new BrushConverter();
        var iconBrush = (System.Windows.Media.Brush)bc.ConvertFromString(iconColor)!;
        dlg.DlgEyebrow.Text = icon switch
        {
            DialogIcon.Error => "Stop",
            DialogIcon.Warning => "Warning",
            DialogIcon.Question => "Confirmation",
            _ => "Information"
        };
        dlg.DlgEyebrow.Foreground = iconBrush;
        dlg.HeaderAccentBar.Background = iconBrush;

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

    private void BtnOK_Click(object sender, RoutedEventArgs e) { Result = "OK"; Close(); }
    private void BtnYes_Click(object sender, RoutedEventArgs e) { Result = "Yes"; Close(); }
    private void BtnNo_Click(object sender, RoutedEventArgs e) { Result = "No"; Close(); }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
        {
            Clipboard.SetText($"{DlgTitle.Text}{Environment.NewLine}{Environment.NewLine}{DlgMessage.Text}");
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
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

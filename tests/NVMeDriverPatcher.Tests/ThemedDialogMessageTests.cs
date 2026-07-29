using System.Reflection;
using System.Windows.Documents;
using System.Windows.Media;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;
using NVMeDriverPatcher.Views;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// Regression coverage for the dialog message layout (GitHub issue #14).
///
/// The opening segment of a dialog message used to render only its first line and
/// silently discard the rest, so every "&lt;thing&gt; saved to:\n&lt;path&gt;" dialog
/// showed the sentence with a blank where the path should be. Users had to hunt the
/// filesystem for the artifact they had just generated.
///
/// Everything runs inside one STA session: WPF allows a single <see cref="System.Windows.Application"/>
/// per AppDomain and binds it to the thread that created it, so per-test STA threads
/// deadlock on the second window.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ThemedDialogMessageTests
{
    [Fact]
    public void DialogMessagesRenderEveryLineOfTheOpeningSegment()
    {
        WpfTestHost.Run(() =>
        {
            // ThemedDialog.xaml resolves theme resources (SectionEyebrow, TextPrimary, …)
            // from application scope; WpfTestHost owns the single Application.
            ThemeService.ApplyMode(AppThemeMode.Dark);

            // Support bundle — the exact dialog from the issue report.
            const string bundlePath = @"C:\ProgramData\NVMePatcher\NVMe_SupportBundle_20260717_163638.zip";
            var bundle = RenderMessage(
                $"A shareable support bundle was saved to:\n{bundlePath}\n\n" +
                "Contains: diagnostics report, config, crash logs, recent registry backups, and the SQLite DB.");
            Assert.Contains("A shareable support bundle was saved to:", bundle);
            Assert.Contains(bundlePath, bundle);
            Assert.Contains("Contains: diagnostics report", bundle);

            // Verification script.
            const string scriptPath = @"C:\ProgramData\NVMePatcher\Verify-NVMePatch.ps1";
            var script = RenderMessage(
                $"Verification script saved to:\n{scriptPath}\n\n" +
                "Run it after reboot to confirm the patch keys and Safe Mode protections are still present.");
            Assert.Contains(scriptPath, script);

            // Recovery kit.
            const string kitDir = @"E:\NVMeRecoveryKit";
            var kit = RenderMessage(
                $"Recovery kit saved to:\n{kitDir}\n\n" +
                "Copy this folder to a USB drive for offline recovery.\nContains .reg file, .bat script, and README.");
            Assert.Contains(kitDir, kit);
            Assert.Contains("Copy this folder to a USB drive", kit);

            // Exported log — a single segment with no blank-line break at all, so the
            // whole path lived in the discarded remainder.
            const string logPath = @"C:\ProgramData\NVMePatcher\nvme-patcher.log";
            Assert.Contains(logPath, RenderMessage($"Log saved to:\n{logPath}"));

            // Multi-segment messages (headings, bullets, decision line) must be unaffected.
            var confirm = RenderMessage(
                "Apply the NVMe patch?\nThis rewrites boot-critical registry state.\n\n" +
                "CRITICAL\n• BitLocker will be suspended\n• A restore point will be created\n\n" +
                "Choose Continue to proceed.");
            Assert.Contains("Apply the NVMe patch?", confirm);
            Assert.Contains("This rewrites boot-critical registry state.", confirm);
            Assert.Contains("CRITICAL", confirm);
            Assert.Contains("BitLocker will be suspended", confirm);
            Assert.Contains("A restore point will be created", confirm);
            Assert.Contains("Choose Continue to proceed.", confirm);
        });
    }

    /// <summary>
    /// Drives the real <c>ThemedDialog.SetMessage</c> layout code and returns the resulting
    /// FlowDocument as plain text. Going through the actual WPF path (rather than a
    /// reimplementation) is the point: the defect was in how blocks were emitted, not in
    /// the message strings. Must be called on the STA thread that owns the Application.
    /// </summary>
    private static string RenderMessage(string message)
    {
        // The ctor is private — the dialog is only reachable through the static Show
        // helper, which is modal and therefore untestable.
        var dialog = (ThemedDialog)Activator.CreateInstance(typeof(ThemedDialog), nonPublic: true)!;

        var setMessage = typeof(ThemedDialog).GetMethod(
            "SetMessage",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(setMessage);
        setMessage!.Invoke(dialog, new object[] { message, Brushes.White });

        var documentField = typeof(ThemedDialog).GetField(
            "DlgMessageDocument",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(documentField);

        var document = Assert.IsType<FlowDocument>(documentField!.GetValue(dialog));
        var text = new TextRange(document.ContentStart, document.ContentEnd).Text;

        // An un-closed Window keeps WPF (and therefore the test host) alive.
        try { dialog.Close(); } catch { }
        return text;
    }

}

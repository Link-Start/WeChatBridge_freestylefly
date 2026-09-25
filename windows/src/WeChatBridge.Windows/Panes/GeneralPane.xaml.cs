using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace WeChatBridge.Windows.Panes;

/// <summary>通用 pane: status, retention window, version.</summary>
public partial class GeneralPane : UserControl
{
    public GeneralPane()
    {
        InitializeComponent();
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0";
        VersionText.Text = $"当前版本 {version}（Windows 移植版）";
    }

    private MainViewModel? Model => DataContext as MainViewModel;

    private void RevealInbox_Click(object sender, RoutedEventArgs e) => Model?.RevealInbox();

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/freestylefly/WeChatBridge",
                UseShellExecute = true,
            });
        }
        catch
        {
            // A missing browser is not worth an error dialog in settings.
        }
    }
}

using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace WeChatBridge.Windows.Panes;

public partial class AboutPane : UserControl
{
    public AboutPane()
    {
        InitializeComponent();
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0";
        VersionText.Text = $"版本 {version} · Windows";
    }

    private void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // A missing browser is not worth an error dialog in settings.
        }
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e) =>
        Open("https://github.com/freestylefly/WeChatBridge");

    private void OpenIssues_Click(object sender, RoutedEventArgs e) =>
        Open("https://github.com/freestylefly/WeChatBridge/issues/new");
}

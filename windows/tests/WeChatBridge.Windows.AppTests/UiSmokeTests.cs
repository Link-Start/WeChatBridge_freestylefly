using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Panes;
using WeChatBridge.Windows.Services;
using WeChatBridge.Windows.Tests;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace WeChatBridge.Windows.AppTests;

public sealed class UiSmokeTests
{
    [Fact]
    public void LocalizedSettingsAndCollectionRenderWithoutStartingTheApp()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var originalLanguage = L10n.Language;
            try
            {
                using var fixture = new TempInbox();
                var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
                var config = Path.Combine(fixture.Root, "config");
                using var scenes = new SceneService(config);
                var model = new MainViewModel(fixture.Paths, config, false, scenes: scenes, copyPayload: _ => { });
                var source = fixture.WriteSource("sample.zip", "sample");
                var batch = InboxWriter.CommitAsync(fixture.Paths,
                    [new InboxSourceFile(source, "sample.zip", "application/zip", 0, 0)]).GetAwaiter().GetResult();
                reader.RecordChatName(batch.BatchId, "示例群聊");
                model.Reload();
                var collectionId = model.Collections.Append(model.Batches.Single(), null);
                var app = new Application();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/WeChatBridge.Windows;component/Themes/AppTheme.xaml"),
                });
                var previewDirectory = Environment.GetEnvironmentVariable("WECHATBRIDGE_UI_PREVIEW");
                foreach (var language in new[] { "zh-Hans", "en" })
                {
                    L10n.Language = language;
                    foreach (var (name, pane) in new (string, FrameworkElement)[]
                    {
                        ("general", new GeneralPane()), ("entries", new EntriesPane()),
                        ("history", new HistoryPane()), ("scenes", new ScenesPane()), ("skills", new SkillsPane()),
                    })
                    {
                        pane.DataContext = model;
                        Render(pane, name, language, previewDirectory);
                    }
                    var window = new CollectionWindow(model, collectionId);
                    window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    var content = (FrameworkElement)window.Content;
                    window.Content = null;
                    Render(content, "collection", language, previewDirectory);
                    window.Close();
                }
                app.Shutdown();
            }
            catch (Exception error) { failure = error; }
            finally { L10n.Language = originalLanguage; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "UI render did not complete");
        Assert.Null(failure);
    }

    private static void Render(FrameworkElement content, string name, string language, string? directory)
    {
        var canvas = new Border { Background = (Brush)Application.Current.Resources["BackgroundColor"], Child = content };
        canvas.Measure(new Size(710, 650));
        canvas.Arrange(new Rect(0, 0, 710, 650));
        canvas.UpdateLayout();
        Assert.True(content.ActualWidth > 0);
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(710, 650, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, $"{name}-{language}.png"));
        encoder.Save(stream);
    }
}

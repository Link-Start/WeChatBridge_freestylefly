using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
                model.Reload();
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
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
                        if (pane is HistoryPane)
                        {
                            var renameButton = Descendants<Button>(pane).Single(b => b.ToolTip?.ToString() == L10n.Text("修改名称"));
                            renameButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            var panel = Descendants<StackPanel>(pane).Single(p => p.Name == "CollectionRenamePanel");
                            Assert.Equal(Visibility.Visible, panel.Visibility);
                            var editor = Descendants<TextBox>(panel).Single();
                            editor.Text = "不保存";
                            Descendants<Button>(panel).Single(b => b.Content?.ToString() == L10n.Text("取消"))
                                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Assert.Equal(Visibility.Collapsed, panel.Visibility);
                            Assert.NotEqual("不保存", model.Collections.Ledger.Editable(collectionId).Name);
                            renameButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            editor.Text = "示例收集";
                            Descendants<Button>(panel).Single(b => b.Content?.ToString() == L10n.Text("保存"))
                                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Assert.Equal("示例收集", model.Collections.Ledger.Editable(collectionId).Name);
                            Assert.Equal("示例收集", model.CollectionRows.Single(r => r.Id == collectionId).Title);
                        }
                    }
                    var window = new CollectionWindow(model, collectionId);
                    window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    Assert.Null(window.FindName("CollectionList"));
                    Assert.Null(window.FindName("CollectionName"));
                    Assert.True(window.Topmost);
                    Assert.False(window.ShowActivated);
                    Assert.False(window.ShowInTaskbar);
                    Assert.Equal(WindowStyle.None, window.WindowStyle);
                    var management = (FrameworkElement)window.FindName("ManagementPanel");
                    var manageButton = (Button)window.FindName("ManageButton");
                    Assert.Equal(Visibility.Collapsed, ((Button)window.FindName("ResumeButton")).Visibility);
                    Assert.Equal(Visibility.Collapsed, management.Visibility);
                    var content = (FrameworkElement)window.Content;
                    content.Resources.MergedDictionaries.Add(window.Resources);
                    window.Content = null;
                    var compactHeight = Render(content, "collection", language, previewDirectory, width: 400);
                    Assert.InRange(compactHeight, 180, 320);
                    manageButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(Visibility.Visible, management.Visibility);
                    var expandedHeight = Render(content, "collection-expanded", language, previewDirectory, width: 400);
                    Assert.True(expandedHeight > compactHeight + 150, "Expanded batch controls must fit within the floating card.");
                    manageButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(Visibility.Collapsed, management.Visibility);
                    window.Content = content;
                    window.Show();
                    Assert.True(window.IsVisible);
                    ((Button)window.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.False(window.IsVisible);
                    Assert.Equal(CollectionStatus.Collecting, model.Collections.Ledger.Editable(collectionId).Status);
                    window.Show();
                    Assert.True(window.IsVisible);
                    window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Escape)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent,
                    });
                    Assert.False(window.IsVisible);
                    // Closing the float must not split the next WeChat share into a new group.
                    var nextBatch = InboxWriter.CommitAsync(fixture.Paths,
                        [new InboxSourceFile(source, "next.zip", "application/zip", 0, 0)]).GetAwaiter().GetResult();
                    var nextId = model.Collections.Append(reader.LoadBatches().Single(b => b.Id == nextBatch.BatchId), "示例群聊");
                    Assert.Equal(collectionId, nextId);
                    Assert.Equal(collectionId, new CollectionService(fixture.Paths, reader).Ledger.Current?.Id);
                    window.Close();
                    Assert.Equal(CollectionStatus.Collecting, model.Collections.Ledger.Editable(collectionId).Status);
                    Assert.True(model.RenameCollection(collectionId, "  示例收集  "));
                    Assert.Equal("示例收集", new CollectionService(fixture.Paths, reader).Ledger.Editable(collectionId).Name);
                    model.Collections.Ledger.ParkCurrent();
                    model.Collections.Save();
                    model.Reload();
                    Assert.Contains(L10n.Text("待发送"), model.CollectionRows.Single(r => r.Id == collectionId).Detail);
                    var draftWindow = new CollectionWindow(model, collectionId);
                    draftWindow.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    Assert.StartsWith(L10n.Text("待发送"), ((TextBlock)draftWindow.FindName("Status")).Text);
                    Assert.Equal(Visibility.Visible, ((Button)draftWindow.FindName("ResumeButton")).Visibility);
                    draftWindow.Close();
                    model.Collections.Ledger.Resume(collectionId);
                    model.Collections.Save();
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

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static double Render(FrameworkElement content, string name, string language, string? directory, double width = 710)
    {
        var canvas = new Border { Background = (Brush)Application.Current.Resources["BackgroundColor"], Child = content };
        var height = 650.0;
        // Reparented elements may retain their previous measurement. Resolve
        // templates and visibility changes before sizing the exported bitmap.
        for (var pass = 0; pass < 2; pass++)
        {
            content.InvalidateMeasure();
            canvas.InvalidateMeasure();
            canvas.Measure(new Size(width, double.PositiveInfinity));
            height = width == 400 ? Math.Ceiling(canvas.DesiredSize.Height) : 650;
            canvas.Arrange(new Rect(0, 0, width, height));
            canvas.UpdateLayout();
        }
        Assert.True(content.ActualWidth > 0);
        if (directory is null) { canvas.Child = null; return height; }
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, $"{name}-{language}.png"));
        encoder.Save(stream);
        canvas.Child = null;
        return height;
    }
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows;

/// <summary>
/// Single-choice scene panel, ported from the macOS <c>ScenePickerPanel</c>:
/// pick one scene and confirm, or continue without one (「直接转发」). The panel
/// expires after <see cref="BatchIntent.FreshnessWindow"/> — a share the user
/// has stopped thinking about must not paste a scene decision a minute later.
/// </summary>
public partial class ScenePickerWindow : Window
{
    private readonly TaskCompletionSource<ScenePickerAnswer> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _deadline;
    private readonly IReadOnlyList<WeChatScene> _scenes;

    public ScenePickerWindow(IReadOnlyList<WeChatScene> scenes)
    {
        _scenes = scenes;
        InitializeComponent();
        SceneList.ItemsSource = scenes.Select(scene => new Row(scene)).ToList();
        SceneList.SelectedIndex = -1;
        _deadline = new DispatcherTimer { Interval = BatchIntent.FreshnessWindow };
        _deadline.Tick += (_, _) => Finish(ScenePickerAnswer.Expired);
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) => Finish(ScenePickerAnswer.Cancelled);
        // Manual placement starts off-screen; the real position is applied once
        // Show has given the window a size and a PresentationSource for DPI math.
        Left = -10000;
        Top = -10000;
    }

    /// <summary>
    /// The coordinator-facing entry point: shows the panel near the cursor and
    /// completes with the answer — picked, cancelled, or expired.
    /// </summary>
    public static Task<ScenePickerAnswer> ChooseAsync(
        IReadOnlyList<WeChatScene> scenes,
        CancellationToken cancellationToken = default,
        Window? owner = null)
    {
        var window = new ScenePickerWindow(scenes);
        if (owner is not null)
            window.Owner = owner;
        cancellationToken.Register(
            () => window.Dispatcher.BeginInvoke(window.Close));
        window._deadline.Start();
        window.Show();
        PositionWindowNearCursor(window);
        window.Activate();
        window.SceneList.Focus();
        return window._completion.Task;
    }

    private static void PositionWindowNearCursor(Window window)
    {
        if (!NativeMethods.GetCursorPos(out var point))
            return;
        var source = PresentationSource.FromVisual(window);
        var toDip = source?.CompositionTarget?.TransformFromDevice
                    ?? Matrix.Identity;
        var dip = toDip.Transform(new Point(point.X, point.Y));
        var workArea = SystemParameters.WorkArea;
        window.Left = Math.Clamp(dip.X + 12, workArea.Left,
            Math.Max(workArea.Left, workArea.Right - window.ActualWidth));
        window.Top = Math.Clamp(dip.Y + 12, workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - window.ActualHeight));
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Arrow keys move the highlight like the macOS panel's keyDown handler.</summary>
    private void MoveSelection(int delta)
    {
        if (_scenes.Count == 0)
            return;
        var next = Math.Clamp(SceneList.SelectedIndex + delta, 0, _scenes.Count - 1);
        SceneList.SelectedIndex = next;
        SceneList.ScrollIntoView(SceneList.Items[next]);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ConfirmButton.IsEnabled = SceneList.SelectedItem is Row;

    private void OnCloseClicked(object sender, RoutedEventArgs e) =>
        Finish(ScenePickerAnswer.Cancelled);

    private void OnDirectClicked(object sender, RoutedEventArgs e) =>
        Finish(ScenePickerAnswer.Cancelled);

    private void OnConfirmClicked(object sender, RoutedEventArgs e) =>
        Confirm();

    private void Confirm()
    {
        if (SceneList.SelectedItem is not Row row)
        {
            Finish(ScenePickerAnswer.Cancelled);
            return;
        }
        Finish(ScenePickerAnswer.Picked(row.Scene));
    }

    private void Finish(ScenePickerAnswer answer)
    {
        if (_completion.TrySetResult(answer))
        {
            _deadline.Stop();
            Close();
        }
    }

    /// <summary>Row wrapper so an empty summary still shows the hint text.</summary>
    private sealed class Row(WeChatScene scene)
    {
        public WeChatScene Scene { get; } = scene;
        public string Name => Scene.Name;
        public string SummaryText =>
            string.IsNullOrWhiteSpace(Scene.Summary) ? "没有一句话说明" : Scene.Summary;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }
    }
}

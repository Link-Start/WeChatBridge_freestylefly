using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Onboarding;

/// <summary>
/// The first-run guide, ported from macOS <c>OnboardingFlow</c> +
/// <c>OnboardingWindowController</c>: welcome → the nine share-menu entries →
/// residency → done.
///
/// Windows asks for no Accessibility permission, so the permission step becomes
/// the residency note — the one choice that decides whether a share forwards
/// instantly. The state is shown rather than toggled: the Run key and
/// settings.json stay in sync only through <c>MainViewModel.AutoStartEnabled</c>,
/// which lives in 通用.
///
/// A window of its own rather than a pane for the macOS reason: none of this is
/// a setting. The whole surface lives inside WeChat's share menu, which a
/// settings window cannot demonstrate.
/// </summary>
public partial class OnboardingWindow : Window
{
    private static readonly string[] StepTitles = ["欢迎", "入口", "驻留", "完成"];
    private const int LastStep = 3;

    private readonly OnboardingStateStore _store;
    private readonly TextBlock[] _barLabels = new TextBlock[StepTitles.Length];
    private readonly Border[] _barRules = new Border[StepTitles.Length];
    private int _step;

    public OnboardingWindow(OnboardingStateStore? store = null)
    {
        _store = store ?? new OnboardingStateStore();
        InitializeComponent();
        BuildStepBar();
        // One list feeds both menus — the pane's roster and the art column's
        // drawing of it — for the same §11.2 reason macOS shares the probe.
        var entries = ShareActions.All.Select(a => a.EntryTitle()).ToList();
        EntriesList.ItemsSource = entries;
        EntriesArtList.ItemsSource = entries;
        ShowAutoStart(LaunchAtLogin.IsRegistered());
        // Resume where a mid-run close stopped, like preferences.onboardingStep;
        // only 完成 returns the counter to the top.
        ShowStep(Math.Clamp(_store.Load().Step, 0, LastStep));
    }

    private void BuildStepBar()
    {
        var tertiary = (Brush)FindResource("InkTertiaryColor");
        var brand = (Brush)FindResource("BrandColor");
        for (var i = 0; i < StepTitles.Length; i++)
        {
            var label = new TextBlock
            {
                Text = StepTitles[i],
                FontSize = 12,
                TextAlignment = TextAlignment.Center,
                Foreground = tertiary,
            };
            var rule = new Border
            {
                Height = 2,
                Margin = new Thickness(10, 7, 10, 0),
                CornerRadius = new CornerRadius(1),
                Background = brand,
                Visibility = Visibility.Collapsed,
            };
            var item = new StackPanel { Width = 64 };
            item.Children.Add(label);
            item.Children.Add(rule);
            StepBarHost.Children.Add(item);
            _barLabels[i] = label;
            _barRules[i] = rule;
            if (i < StepTitles.Length - 1)
            {
                StepBarHost.Children.Add(new TextBlock
                {
                    Text = "›",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = tertiary,
                    Margin = new Thickness(4, 0, 4, 0),
                });
            }
        }
    }

    private void ShowStep(int step)
    {
        _step = step;
        var panels = new UIElement[] { StepWelcome, StepEntries, StepResidency, StepDone };
        for (var i = 0; i < panels.Length; i++)
            panels[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;

        // The art column draws what the step is about — the same switch the
        // macOS `art` ZStack makes: brand mark, menu, note, menu again.
        ArtWelcome.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        ArtMenu.Visibility = step is 1 or 3 ? Visibility.Visible : Visibility.Collapsed;
        ArtNote.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;

        var ink = (Brush)FindResource("InkColor");
        var tertiary = (Brush)FindResource("InkTertiaryColor");
        for (var i = 0; i < StepTitles.Length; i++)
        {
            _barLabels[i].Foreground = i == step ? ink : tertiary;
            _barLabels[i].FontWeight = i == step ? FontWeights.SemiBold : FontWeights.Regular;
            _barRules[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;
        }

        var last = step == LastStep;
        Footer.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
        BackButton.Visibility = step > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Return always means forward — the macOS defaultAction shortcut.
        NextButton.IsDefault = !last;
        DoneButton.IsDefault = last;
        _store.SaveStep(step);
    }

    /// <summary>The ChecklistRow port: the residency switch, shown not offered.</summary>
    private void ShowAutoStart(bool registered)
    {
        var surface = (Brush)FindResource("SurfaceColor");
        var stroke = (Brush)FindResource("StrokeColor");
        var brand = (Brush)FindResource("BrandColor");
        AutoStartTick.Background = registered ? brand : surface;
        AutoStartTick.BorderBrush = registered ? brand : stroke;
        AutoStartTickText.Visibility = registered ? Visibility.Visible : Visibility.Collapsed;
        AutoStartPill.Background = (Brush)FindResource(registered ? "LiveFillColor" : "NeutralFillColor");
        AutoStartPillText.Foreground = (Brush)FindResource(registered ? "LiveInkColor" : "NeutralInkColor");
        AutoStartPillText.Text = registered ? "已开启" : "未开启";
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e) => DragMove();

    /// <summary>Closing mid-run is not finishing — the guide comes back next launch.</summary>
    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnBackClicked(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    private void OnNextClicked(object sender, RoutedEventArgs e) => ShowStep(_step + 1);

    private void OnDoneClicked(object sender, RoutedEventArgs e)
    {
        _store.MarkCompleted();
        Close();
    }
}

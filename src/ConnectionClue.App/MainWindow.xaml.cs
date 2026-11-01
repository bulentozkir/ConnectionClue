using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using ConnectionClue.Presentation.Accessibility;
using ConnectionClue.Presentation.ViewModels;

namespace ConnectionClue.App;

public partial class MainWindow : Window
{
    private readonly CultureInfo _ui;

    public MainWindow(MainViewModel vm, CultureInfo ui)
    {
        InitializeComponent();
        _ui = ui;
        DataContext = vm;
        Language = XmlLanguage.GetLanguage(ui.IetfLanguageTag); // correct glyphs (Han variants) and UIA culture
        FlowDirection = ui.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);
        // Until the first frame is drawn (a moment when the window is built anew), the surface shows its own colour, white by
        // default: take the theme's, so a dark theme does not flash white.
        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is HwndSource { CompositionTarget: { } target })
                target.BackgroundColor = (TryFindResource("ApplicationBackgroundBrush") as SolidColorBrush)?.Color ?? SystemColors.WindowColor;
        };
        CheckSecondsBox.LostKeyboardFocus += (_, _) => vm.Settings.CommitCheckSecondsText();
        BackgroundCheckSecondsBox.LostKeyboardFocus += (_, _) => vm.Settings.CommitBackgroundCheckSecondsText();
        PlanDownloadBox.LostKeyboardFocus += (_, _) => vm.Settings.CommitPlanSpeedText();
        PlanUploadBox.LostKeyboardFocus += (_, _) => vm.Settings.CommitPlanSpeedText();
        IsVisibleChanged += (_, _) => vm.SetWindowVisible(IsVisible);
        vm.SetWindowVisible(IsVisible);
        // The view model outlives the window (it is released in the notification area), so the window must let go of it.
        EventHandler<Announcement> announce = (_, a) => Dispatcher.Invoke(() =>
        {
            if (!IsVisible || !IsActive) return;
            (UIElementAutomationPeer.FromElement(this) ?? UIElementAutomationPeer.CreatePeerForElement(this))
                ?.RaiseNotificationEvent(AutomationNotificationKind.Other,
                    a.Urgency == AnnouncementUrgency.Immediate
                        ? AutomationNotificationProcessing.ImportantMostRecent
                        : AutomationNotificationProcessing.MostRecent,
                    a.Text, "ConnectionClue");
        });
        vm.Announce += announce;
        Closed += (_, _) => vm.Announce -= announce;
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        var help = new HelpWindow((MainViewModel)DataContext, _ui) { Owner = this };
        help.ShowDialog();
    }

    /// <summary>The visuals the results PDF shows: the whole Check page, then the delay chart at full width.</summary>
    public IReadOnlyList<FrameworkElement> ResultVisuals => [CheckPage, ChartCard];

    private static readonly System.Windows.Input.Key[] ArrowKeys =
        [System.Windows.Input.Key.Left, System.Windows.Input.Key.Right, System.Windows.Input.Key.Up, System.Windows.Input.Key.Down];

    // As in any Windows radio group, moving to a choice with the arrow keys also selects it. Tab only enters the group.
    private void SymptomRadio_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.RadioButton { IsChecked: not true } radio) return;
        if (!ArrowKeys.Any(System.Windows.Input.Keyboard.IsKeyDown)) return;
        radio.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true); // keeps the binding
        if (radio.Command?.CanExecute(radio.CommandParameter) == true) radio.Command.Execute(radio.CommandParameter);
    }
}
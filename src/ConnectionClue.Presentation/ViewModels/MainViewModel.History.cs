using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConnectionClue.Presentation.Accessibility;

namespace ConnectionClue.Presentation.ViewModels;

/// <summary>Clearing the saved check summaries behind Insights (Settings). Two steps, because it cannot be undone.</summary>
public sealed partial class MainViewModel
{
    /// <summary>The user asked to clear the history; the confirm and cancel buttons show until they choose.</summary>
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ClearHistoryCommand))]
    public partial bool IsConfirmingClearHistory { get; set; }

    /// <summary>"History cleared." after clearing; empty otherwise.</summary>
    [ObservableProperty]
    public partial string HistoryClearStatus { get; set; } = "";

    /// <summary>How many summaries are saved, for the Settings card and the confirmation.</summary>
    public string HistoryCountText => string.Format(_ui, _l.Get("History_ClearHelp", _ui), _history.Count);

    public string ClearHistoryConfirmText => string.Format(_ui, _l.Get("History_ClearConfirm", _ui), _history.Count);

    private bool CanClearHistory() => _history.Count > 0 && !IsConfirmingClearHistory;

    [RelayCommand(CanExecute = nameof(CanClearHistory))]
    private void ClearHistory()
    {
        (IsConfirmingClearHistory, HistoryClearStatus) = (true, "");
        OnPropertyChanged(nameof(ClearHistoryConfirmText));
        Emit(ClearHistoryConfirmText, AnnouncementKind.SessionState);
    }

    [RelayCommand]
    private void ConfirmClearHistory()
    {
        _history = [];
        _historyStore?.Save(_history);
        RefreshHistory();
        (IsConfirmingClearHistory, HistoryClearStatus) = (false, _l.Get("History_Cleared", _ui));
        Emit(HistoryClearStatus, AnnouncementKind.SessionState);
    }

    [RelayCommand]
    private void CancelClearHistory() => IsConfirmingClearHistory = false;
}

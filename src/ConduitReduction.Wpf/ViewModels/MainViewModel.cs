using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using ConduitReduction.Wpf.Services;

namespace ConduitReduction.Wpf.ViewModels;

public class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? p) => canExecute?.Invoke() ?? true;
    public void Execute(object? p) => execute();
    public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public class MainViewModel : INotifyPropertyChanged
{
    private readonly ReductionService _svc = new();

    // ── Bindable properties ───────────────────────────────────────────────────
    private string _inputText = string.Empty;
    public string InputText
    {
        get => _inputText;
        set { _inputText = value; OnPropertyChanged(); UpdateCanReduce(); }
    }

    private string _outputText = string.Empty;
    public string OutputText
    {
        get => _outputText;
        set { _outputText = value; OnPropertyChanged(); UpdateCanAccept(); }
    }

    private string _statsLine = "Paste your prompt above and click Reduce";
    public string StatsLine
    {
        get => _statsLine;
        set { _statsLine = value; OnPropertyChanged(); }
    }

    private string _passLog = string.Empty;
    public string PassLog
    {
        get => _passLog;
        set { _passLog = value; OnPropertyChanged(); }
    }

    private bool _hasResult;
    public bool HasResult
    {
        get => _hasResult;
        set { _hasResult = value; OnPropertyChanged(); }
    }

    private double _reductionPct;
    public double ReductionPct
    {
        get => _reductionPct;
        set { _reductionPct = value; OnPropertyChanged(); }
    }

    // ── Commands ──────────────────────────────────────────────────────────────
    public RelayCommand ReduceCommand { get; }
    public RelayCommand AcceptCommand { get; }
    public RelayCommand ClearCommand  { get; }

    public MainViewModel()
    {
        ReduceCommand = new RelayCommand(DoReduce,  () => !string.IsNullOrWhiteSpace(InputText));
        AcceptCommand = new RelayCommand(DoAccept,  () => !string.IsNullOrWhiteSpace(OutputText));
        ClearCommand  = new RelayCommand(DoClear);
    }

    private void DoReduce()
    {
        if (string.IsNullOrWhiteSpace(InputText)) return;

        var result = _svc.Reduce(InputText);

        OutputText   = result.Compressed;
        ReductionPct = result.ReductionPct;
        StatsLine    = $"{result.OriginalTokens:N0} tok  →  {result.CompressedTokens:N0} tok  ·  {result.ReductionPct:F1}% saved";
        PassLog      = string.Join("\n", result.PassLog);
        HasResult    = true;
    }

    private void DoAccept()
    {
        if (string.IsNullOrWhiteSpace(OutputText)) return;
        Clipboard.SetText(OutputText);
        StatsLine = "✓  Compressed text copied — paste into your chat";
    }

    private void DoClear()
    {
        InputText  = string.Empty;
        OutputText = string.Empty;
        PassLog    = string.Empty;
        StatsLine  = "Paste your prompt above and click Reduce";
        HasResult  = false;
        ReductionPct = 0;
    }

    private void UpdateCanReduce() => ReduceCommand.Raise();
    private void UpdateCanAccept() => AcceptCommand.Raise();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

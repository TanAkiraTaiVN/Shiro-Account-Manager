#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro.ViewModels.Dialogs;

public sealed class AltGenViewModel : DialogViewModelBase
{
    private readonly IBloxGenService _bloxGen;
    private readonly IToastNotifier _toasts;
    private readonly Action<string> _openFile;
    private readonly string _historyPath;

    private string _apiKey;
    private string _selectedContinent;
    private string _selectedGenType;
    private string _groupName = "AltGen";
    private bool _hasResult;
    private string _resultUsername = string.Empty;
    private string _resultUserId = string.Empty;
    private string _resultPassword = string.Empty;

    public AltGenViewModel(
        IBloxGenService? bloxGen = null,
        IToastNotifier? toasts = null,
        Action<string>? openFile = null,
        string? historyPath = null)
    {
        _bloxGen = bloxGen ?? AppServices.BloxGen;
        _toasts = toasts ?? AppServices.Toasts;
        _openFile = openFile ?? DefaultOpenFile;
        _historyPath = historyPath ?? Path.Combine(StoragePaths.DataDirectory, "altgen_history.csv");

        _apiKey = _bloxGen.SavedApiKey;
        _selectedContinent = string.IsNullOrWhiteSpace(_bloxGen.DefaultContinent) ? "RANDOM" : _bloxGen.DefaultContinent;
        _selectedGenType = string.IsNullOrWhiteSpace(_bloxGen.DefaultGenType) ? "unverified" : _bloxGen.DefaultGenType;

        SetStatus("Sẵn sàng tạo tài khoản", StatusKind.Neutral);

        GenerateCommand = TrackCommand(new AsyncRelayCommand(GenerateAsync, () => !IsBusy));
        OpenHistoryCommand = new RelayCommand(OpenHistory);
        CloseCommand = new RelayCommand(() => RequestClose(null));
    }

    public string ApiKey
    {
        get => _apiKey;
        set => SetProperty(ref _apiKey, value);
    }

    public string SelectedContinent
    {
        get => _selectedContinent;
        set => SetProperty(ref _selectedContinent, string.IsNullOrWhiteSpace(value) ? "RANDOM" : value);
    }

    public string SelectedGenType
    {
        get => _selectedGenType;
        set => SetProperty(ref _selectedGenType, string.IsNullOrWhiteSpace(value) ? "unverified" : value);
    }

    public string GroupName
    {
        get => _groupName;
        set => SetProperty(ref _groupName, value);
    }

    public bool HasResult
    {
        get => _hasResult;
        private set => SetProperty(ref _hasResult, value);
    }

    public string ResultUsername
    {
        get => _resultUsername;
        private set => SetProperty(ref _resultUsername, value);
    }

    public string ResultUserId
    {
        get => _resultUserId;
        private set => SetProperty(ref _resultUserId, value);
    }

    public string ResultPassword
    {
        get => _resultPassword;
        private set => SetProperty(ref _resultPassword, value);
    }

    public IAsyncRelayCommand GenerateCommand { get; }
    public ICommand OpenHistoryCommand { get; }
    public ICommand CloseCommand { get; }

    private async Task GenerateAsync()
    {
        if (IsBusy)
            return;

        string key = _apiKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            SetStatus(LocalizationService.Get("AltGen.EnterApiKey"), StatusKind.Warning);
            return;
        }

        string genType = string.IsNullOrWhiteSpace(_selectedGenType) ? "unverified" : _selectedGenType;
        string continent = string.IsNullOrWhiteSpace(_selectedContinent) ? "RANDOM" : _selectedContinent;
        string group = !string.IsNullOrWhiteSpace(_groupName) ? _groupName.Trim() : "AltGen";

        _bloxGen.SavedApiKey = key;
        _bloxGen.DefaultGenType = genType;
        _bloxGen.DefaultContinent = continent;

        IsBusy = true;
        CancellationToken ct = BeginOperation();
        SetStatus(LocalizationService.Get("AltGen.ConnectingApi"), StatusKind.Info);
        HasResult = false;

        try
        {
            BloxGenAccountResult result = await _bloxGen.GenerateAccountAsync(key, genType, continent, ct).ConfigureAwait(true);
            if (result.Success && result.Account != null)
            {
                bool imported = _bloxGen.ImportAccountToShiro(result.Account, group);

                ResultUsername = result.Account.Username;
                ResultUserId = result.Account.UserId > 0
                    ? result.Account.UserId.ToString()
                    : LocalizationService.Get("AltGen.LoadingState");
                ResultPassword = !string.IsNullOrWhiteSpace(result.Account.Password)
                    ? result.Account.Password
                    : LocalizationService.Get("AltGen.NoneState");
                HasResult = true;

                if (imported)
                {
                    SetStatus(LocalizationService.Format("AltGen.CreateSuccessWithGroup", group), StatusKind.Success);
                }
                else
                {
                    SetStatus(LocalizationService.Get("AltGen.SaveToShiroFailed"), StatusKind.Warning);
                }

                _toasts.ShowSuccess(
                    LocalizationService.Get("AltGen.CreateSuccessTitle"),
                    LocalizationService.Format("AltGen.AccountAdded", result.Account.Username));
            }
            else
            {
                string err = result.ErrorMessage ?? LocalizationService.Get("AltGen.UnknownError");
                SetStatus(LocalizationService.Format("AltGen.FailedFormat", err), StatusKind.Error);
                _toasts.ShowError(
                    LocalizationService.Get("AltGen.CreateFailedTitle"),
                    result.ErrorMessage ?? LocalizationService.Get("AltGen.CreateFailedGeneric"));
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled on dialog close
        }
        catch (Exception ex)
        {
            SetStatus(LocalizationService.Format("Common.ErrorFormat", ex.Message), StatusKind.Error);
            _toasts.ShowError(LocalizationService.Get("AltGen.SystemErrorTitle"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenHistory()
    {
        try
        {
            if (File.Exists(_historyPath))
            {
                _openFile(_historyPath);
            }
            else
            {
                SetStatus(LocalizationService.Get("AltGen.NoHistory"), StatusKind.Warning);
            }
        }
        catch (Exception ex)
        {
            SetStatus(LocalizationService.Format("AltGen.CannotOpenCsv", ex.Message), StatusKind.Error);
        }
    }

    private static void DefaultOpenFile(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}

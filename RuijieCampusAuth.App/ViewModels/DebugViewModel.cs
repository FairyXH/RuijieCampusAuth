using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RuijieCampusAuth.Core;
using RuijieCampusAuth.Core.Debug;

namespace RuijieCampusAuth.App.ViewModels;

public partial class DebugViewModel : ViewModelBase
{
    private readonly UserStore _store;
    private readonly DebugGateway _gateway;

    [ObservableProperty] private DebugFeatureDescriptor? _selectedFeature;
    [ObservableProperty] private string _selectedTitle = "选择一个调试功能";
    [ObservableProperty] private string _selectedDescription = "";
    [ObservableProperty] private string _selectedMeta = "";
    [ObservableProperty] private string _argumentsJson = "{}";
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string _status = "调试功能未加载";
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _featureEnabled;
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<DebugFeatureDescriptor> Features { get; } = [];

    public DebugViewModel(UserStore? store = null)
    {
        _store = store ?? new UserStore();
        _gateway = new DebugGateway(_store);
        Enabled = _store.LoadSettings().Debug.Enabled;
        _ = RefreshAsync();
    }

    partial void OnSelectedFeatureChanged(DebugFeatureDescriptor? value)
    {
        if (value is null)
        {
            SelectedTitle = "选择一个调试功能";
            SelectedDescription = "";
            SelectedMeta = "";
            FeatureEnabled = false;
            return;
        }
        SelectedTitle = value.DisplayName;
        SelectedDescription = value.Description;
        SelectedMeta =
            $"{value.Category} · {value.Phase} · {value.State}" +
            (value.RequiresService ? " · 需要服务后端" : "");
        FeatureEnabled = value.Enabled;
        // Pre-fill arguments from the current school configuration so the
        // user sees (and can tweak) exactly what the server-side fallback
        // would use.
        ArgumentsJson = DebugArgumentDefaults.Build(
            value.Name,
            _store.LoadSettings(),
            _store.ActiveAccount());
        ResultText = string.IsNullOrWhiteSpace(value.Detail) ? "" : value.Detail;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var result = await _gateway.ListAsync();
            var selected = SelectedFeature?.Name;
            Features.Clear();
            foreach (var item in result.Features)
            {
                Features.Add(item);
            }
            SelectedFeature = Features.FirstOrDefault(item => item.Name == selected)
                ?? Features.FirstOrDefault();
            Status = $"已加载 {Features.Count} 项 · 来源 {result.Source}";
        }
        catch (Exception ex)
        {
            Status = $"加载失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var settings = _store.LoadSettings();
            settings.Debug.Enabled = Enabled;
            if (SelectedFeature is not null)
            {
                settings.Debug.SetFeature(SelectedFeature.Name, FeatureEnabled);
            }
            _store.SaveSettings(settings);
            Status = "调试开关已保存";
            var previousResult = ResultText;
            await RefreshAsync();
            // Keep an already displayed execution result on screen.
            ResultText = previousResult;
        }
        catch (Exception ex)
        {
            Status = $"保存失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (SelectedFeature is null || IsBusy)
        {
            return;
        }
        IsBusy = true;
        Status = $"正在执行 {SelectedFeature.DisplayName} ...";
        try
        {
            var result = await _gateway.RunAsync(
                SelectedFeature.Name,
                ArgumentsJson);
            // Refresh first (it rebuilds the feature list and resets the
            // selection, which clears ResultText), then write the output so
            // it stays visible.
            await RefreshAsync();
            ResultText = FormatResult(result);
            Status = result.Ok ? "执行成功" : "执行失败";
        }
        catch (Exception ex)
        {
            ResultText = ex.ToString();
            Status = "执行异常";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (SelectedFeature is null || IsBusy)
        {
            return;
        }
        IsBusy = true;
        try
        {
            var result = await _gateway.StopAsync(SelectedFeature.Name);
            ResultText = FormatResult(result);
            Status = result.Ok ? "已停止" : "停止失败";
        }
        catch (Exception ex)
        {
            ResultText = ex.ToString();
            Status = "停止异常";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenLog()
    {
        try
        {
            var path = Path.Combine(_store.BaseDirectory, "logs", "debug.log");
            if (!File.Exists(path))
            {
                Status = "调试日志尚未生成";
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = $"打开日志失败: {ex.Message}";
        }
    }

    private static string FormatResult(DebugFeatureResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine(result.Ok ? "OK" : "FAILED");
        builder.AppendLine(result.Message);
        if (!string.IsNullOrWhiteSpace(result.Data))
        {
            builder.AppendLine();
            builder.AppendLine(Pretty(result.Data));
        }
        builder.AppendLine();
        builder.Append($"完成于 {result.FinishedAt:yyyy-MM-dd HH:mm:ss}");
        return builder.ToString();
    }

    private static string Pretty(string json)
    {
        try
        {
            return JsonNode.Parse(json)?.ToJsonString(
                       new JsonSerializerOptions { WriteIndented = true })
                   ?? json;
        }
        catch (JsonException)
        {
            return json;
        }
    }
}

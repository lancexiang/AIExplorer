using AIExplorer.Core.Files;
using AIExplorer.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace AIExplorer_App.ViewModels;

/// <summary>
/// 文件列表列显隐与宽度。窗格与每一行共享同一个实例。
/// 隐藏时列宽置 0 且内容 Collapsed。
/// </summary>
public partial class FileColumnLayout : ObservableObject
{
    public const double SizeMin = 56;
    public const double SizeMax = 200;
    public const double TypeMin = 48;
    public const double TypeMax = 160;
    public const double ModifiedMin = 120;
    public const double ModifiedMax = 320;

    private readonly ISettingsService _settings;
    private CancellationTokenSource? _saveCts;

    public FileColumnLayout(ISettingsService settings)
    {
        _settings = settings;
        showSize = settings.Features.ShowSizeColumn;
        showType = settings.Features.ShowTypeColumn;
        showModified = settings.Features.ShowModifiedColumn;
        sizePixels = Clamp(settings.Features.SizeColumnWidth, SizeMin, SizeMax, 90);
        typePixels = Clamp(settings.Features.TypeColumnWidth, TypeMin, TypeMax, 70);
        modifiedPixels = Clamp(settings.Features.ModifiedColumnWidth, ModifiedMin, ModifiedMax, 200);
    }

    [ObservableProperty]
    private bool showSize;

    [ObservableProperty]
    private bool showType;

    [ObservableProperty]
    private bool showModified;

    /// <summary>名称文本最大宽度（名称列一半），随窗格变宽自适应。</summary>
    [ObservableProperty]
    private double nameTextMaxWidth = 160;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeWidth))]
    private double sizePixels;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypeWidth))]
    private double typePixels;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModifiedWidth))]
    private double modifiedPixels;

    public GridLength SizeWidth => ShowSize ? new GridLength(SizePixels) : new GridLength(0);
    public GridLength TypeWidth => ShowType ? new GridLength(TypePixels) : new GridLength(0);
    public GridLength ModifiedWidth => ShowModified ? new GridLength(ModifiedPixels) : new GridLength(0);

    public Visibility SizeVisibility => ShowSize ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TypeVisibility => ShowType ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ModifiedVisibility => ShowModified ? Visibility.Visible : Visibility.Collapsed;

    public void SetSizePixels(double value, bool persist)
    {
        SizePixels = Clamp(value, SizeMin, SizeMax, SizePixels);
        if (persist)
        {
            SchedulePersist();
        }
    }

    public void SetTypePixels(double value, bool persist)
    {
        TypePixels = Clamp(value, TypeMin, TypeMax, TypePixels);
        if (persist)
        {
            SchedulePersist();
        }
    }

    public void SetModifiedPixels(double value, bool persist)
    {
        ModifiedPixels = Clamp(value, ModifiedMin, ModifiedMax, ModifiedPixels);
        if (persist)
        {
            SchedulePersist();
        }
    }

    partial void OnShowSizeChanged(bool value)
    {
        _settings.Features.ShowSizeColumn = value;
        SchedulePersist();
        OnPropertyChanged(nameof(SizeWidth));
        OnPropertyChanged(nameof(SizeVisibility));
    }

    partial void OnShowTypeChanged(bool value)
    {
        _settings.Features.ShowTypeColumn = value;
        SchedulePersist();
        OnPropertyChanged(nameof(TypeWidth));
        OnPropertyChanged(nameof(TypeVisibility));
    }

    partial void OnShowModifiedChanged(bool value)
    {
        _settings.Features.ShowModifiedColumn = value;
        SchedulePersist();
        OnPropertyChanged(nameof(ModifiedWidth));
        OnPropertyChanged(nameof(ModifiedVisibility));
    }

    private void SchedulePersist()
    {
        _settings.Features.SizeColumnWidth = SizePixels;
        _settings.Features.TypeColumnWidth = TypePixels;
        _settings.Features.ModifiedColumnWidth = ModifiedPixels;
        _saveCts?.Cancel();
        _saveCts = new CancellationTokenSource();
        var token = _saveCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, token);
                await _settings.SaveAsync(token);
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private static double Clamp(double value, double min, double max, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            return fallback;
        }

        return Math.Clamp(value, min, max);
    }
}

using Microsoft.UI.Xaml.Controls;
using Slice.Models;

namespace Slice.Dialogs;

public sealed partial class ExportDialog : ContentDialog
{
    public ExportDialog()
    {
        InitializeComponent();
        FormatBox.SelectionChanged += (_, _) => RefreshAvailability();
    }

    public ExportOptions GetOptions()
    {
        var format = FormatBox.SelectedIndex switch
        {
            1 => ExportFormat.WebM,
            2 => ExportFormat.Mkv,
            3 => ExportFormat.Gif,
            4 => ExportFormat.Mp3,
            5 => ExportFormat.Wav,
            _ => ExportFormat.Mp4
        };

        int? height = ResolutionBox.SelectedIndex switch
        {
            1 => 1080,
            2 => 720,
            3 => 480,
            _ => null
        };

        double? fps = FpsBox.SelectedIndex switch
        {
            1 => 60,
            2 => 30,
            3 => 24,
            4 => 15,
            _ => null
        };

        var quality = QualityBox.SelectedIndex switch
        {
            0 => ExportQuality.Small,
            2 => ExportQuality.High,
            _ => ExportQuality.Balanced
        };

        return new ExportOptions(format, height, fps, quality, FastCopyBox.IsChecked == true);
    }

    private void RefreshAvailability()
    {
        var audioOnly = FormatBox.SelectedIndex is 4 or 5;
        ResolutionBox.IsEnabled = !audioOnly;
        FpsBox.IsEnabled = !audioOnly;
        FastCopyBox.IsEnabled = FormatBox.SelectedIndex is 0 or 2;
    }
}

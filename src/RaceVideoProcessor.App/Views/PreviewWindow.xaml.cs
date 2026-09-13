using System.Windows;
using System.Windows.Media.Imaging;
using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.App.Views;

public partial class PreviewWindow : Window
{
    public PreviewWindow(PreviewResult preview, string cardNumber)
    {
        InitializeComponent();
        Title = $"Overlay preview — {cardNumber}";
        CardText.Text = cardNumber;
        NormalImage.Source = LoadBitmap(preview.NormalPreviewPath);
        FinalImage.Source = LoadBitmap(preview.FinalPreviewPath);
        MetadataText.Text = $"{preview.SourceMetadata.Width}×{preview.SourceMetadata.Height} • " +
                            $"{preview.SourceMetadata.FrameRate:0.##} fps • " +
                            $"{preview.SourceMetadata.Duration:mm\\:ss}";
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

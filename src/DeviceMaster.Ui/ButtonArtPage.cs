using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace DeviceMaster.Ui;

/// <summary>
/// The Button Art tab: drop in artwork, drag a square crop over it, export at the native
/// key resolution of a Stream Deck LCD button (72×72 for the 15-key / MK.2) to a folder and
/// file name the user picks.
/// </summary>
public sealed class ButtonArtPage
{
    private const int ExportSize = 72;
    private const double CanvasSize = 420;
    private const double MinCropSide = 24;

    private BitmapSource? _source;
    private double _displayWidth, _displayHeight;
    private double _cropX, _cropY, _cropSide;
    private bool _draggingSquare, _resizingSquare;
    private Point _dragStart;
    private double _dragStartX, _dragStartY, _dragStartSide;

    private Border _dropZone = null!;
    private Grid _cropHost = null!;
    private Canvas _cropCanvas = null!;
    private Image _sourceImage = null!;
    private Border _cropSquare = null!;
    private Border _shadeTop = null!, _shadeBottom = null!, _shadeLeft = null!, _shadeRight = null!;
    private Image _previewImage = null!;
    private TextBlock _fileHint = null!;
    private Border _swapButton = null!;

    private TextBox _folderBox = null!;
    private TextBox _nameBox = null!;
    private TextBlock _saveStatus = null!;
    private string _destFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

    public UIElement Build()
    {
        var page = new StackPanel { MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(MainWindow.PageTitle("Button Art", "crop artwork to fit your Stream Deck's LCD keys"));

        var card = Theme.CardShell("⛶", "Crop & Export",
            $"drag the square, then Create — output is {ExportSize}×{ExportSize} px",
            out var body, out _);
        card.HorizontalAlignment = HorizontalAlignment.Left;

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = BuildImageColumn();
        Grid.SetColumn(left, 0);
        columns.Children.Add(left);

        var right = BuildPreviewColumn();
        right.Margin = new Thickness(28, 0, 0, 0);
        Grid.SetColumn(right, 1);
        columns.Children.Add(right);

        body.Children.Add(columns);
        body.Children.Add(BuildSaveSection());
        page.Children.Add(card);
        return page;
    }

    // ---------- left column: drop zone / crop canvas ----------

    private UIElement BuildImageColumn()
    {
        var column = new StackPanel();
        var host = new Grid { Width = CanvasSize, Height = CanvasSize };

        var dropContent = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        dropContent.Children.Add(new TextBlock { Text = "⛶", FontSize = 40, Foreground = Theme.Faint, HorizontalAlignment = HorizontalAlignment.Center });
        dropContent.Children.Add(new TextBlock { Text = "Drop an image here", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, Margin = new Thickness(0, 10, 0, 2), HorizontalAlignment = HorizontalAlignment.Center });
        dropContent.Children.Add(new TextBlock { Text = "or click to browse", FontSize = 12, Foreground = Theme.Faint, HorizontalAlignment = HorizontalAlignment.Center });

        _dropZone = new Border
        {
            Width = CanvasSize,
            Height = CanvasSize,
            CornerRadius = new CornerRadius(12),
            Background = Theme.Inset,
            BorderBrush = Theme.Line2,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = dropContent,
        };
        _dropZone.MouseLeftButtonUp += (_, _) => OnBrowseImageClicked();
        host.Children.Add(_dropZone);

        _cropHost = new Grid { Width = CanvasSize, Height = CanvasSize, Visibility = Visibility.Collapsed };
        _cropHost.Children.Add(new Border
        {
            Width = CanvasSize,
            Height = CanvasSize,
            CornerRadius = new CornerRadius(12),
            Background = Theme.Ink,
        });

        _sourceImage = new Image
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Uniform,
        };
        _cropHost.Children.Add(_sourceImage);

        _cropCanvas = new Canvas
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        BuildCropOverlay();
        _cropHost.Children.Add(_cropCanvas);
        host.Children.Add(_cropHost);

        SetupDragDrop(host);
        column.Children.Add(host);

        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        _fileHint = new TextBlock { FontSize = 11.5, Foreground = Theme.Faint, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_fileHint, Dock.Left);
        footer.Children.Add(_fileHint);
        _swapButton = Theme.Btn("⤢  Choose different image", primary: false, OnBrowseImageClicked);
        _swapButton.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_swapButton, Dock.Right);
        footer.Children.Add(_swapButton);
        column.Children.Add(footer);

        return column;
    }

    /// <summary>The square crop selector: a draggable highlighted square, a resize handle at its
    /// bottom-right corner, and four dimmed panels covering everything outside it.</summary>
    private void BuildCropOverlay()
    {
        _shadeTop = Shade();
        _shadeBottom = Shade();
        _shadeLeft = Shade();
        _shadeRight = Shade();
        foreach (var shade in new[] { _shadeTop, _shadeBottom, _shadeLeft, _shadeRight })
        {
            _cropCanvas.Children.Add(shade);
        }

        var handle = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = Theme.Text,
            BorderBrush = Theme.Accent,
            BorderThickness = new Thickness(2),
            Cursor = Cursors.SizeNWSE,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -8, -8),
        };
        var squareContent = new Grid();
        squareContent.Children.Add(handle);

        _cropSquare = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(40, 0xff, 0xb0, 0x8f)),
            BorderBrush = Theme.Accent,
            BorderThickness = new Thickness(2),
            Cursor = Cursors.SizeAll,
            Child = squareContent,
        };
        _cropCanvas.Children.Add(_cropSquare);

        _cropSquare.MouseLeftButtonDown += (_, e) =>
        {
            _draggingSquare = true;
            _dragStart = e.GetPosition(_cropCanvas);
            _dragStartX = _cropX;
            _dragStartY = _cropY;
            _cropSquare.CaptureMouse();
            e.Handled = true;
        };
        _cropSquare.MouseMove += (_, e) =>
        {
            if (!_draggingSquare)
            {
                return;
            }

            var pos = e.GetPosition(_cropCanvas);
            _cropX = Math.Clamp(_dragStartX + (pos.X - _dragStart.X), 0, Math.Max(_displayWidth - _cropSide, 0));
            _cropY = Math.Clamp(_dragStartY + (pos.Y - _dragStart.Y), 0, Math.Max(_displayHeight - _cropSide, 0));
            ApplyCropVisual();
            UpdatePreview();
        };
        _cropSquare.MouseLeftButtonUp += (_, _) =>
        {
            _draggingSquare = false;
            _cropSquare.ReleaseMouseCapture();
        };

        handle.MouseLeftButtonDown += (_, e) =>
        {
            _resizingSquare = true;
            _dragStart = e.GetPosition(_cropCanvas);
            _dragStartSide = _cropSide;
            handle.CaptureMouse();
            e.Handled = true; // stop this bubbling into _cropSquare's own drag handler
        };
        handle.MouseMove += (_, e) =>
        {
            if (!_resizingSquare)
            {
                return;
            }

            var pos = e.GetPosition(_cropCanvas);
            var delta = Math.Max(pos.X - _dragStart.X, pos.Y - _dragStart.Y);
            var maxSide = Math.Max(Math.Min(_displayWidth - _cropX, _displayHeight - _cropY), MinCropSide);
            _cropSide = Math.Clamp(_dragStartSide + delta, MinCropSide, maxSide);
            ApplyCropVisual();
            UpdatePreview();
        };
        handle.MouseLeftButtonUp += (_, _) =>
        {
            _resizingSquare = false;
            handle.ReleaseMouseCapture();
        };
    }

    private static Border Shade() => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
        IsHitTestVisible = false,
    };

    private void ApplyCropVisual()
    {
        _cropSquare.Width = _cropSide;
        _cropSquare.Height = _cropSide;
        Canvas.SetLeft(_cropSquare, _cropX);
        Canvas.SetTop(_cropSquare, _cropY);

        Canvas.SetLeft(_shadeTop, 0);
        Canvas.SetTop(_shadeTop, 0);
        _shadeTop.Width = _displayWidth;
        _shadeTop.Height = Math.Max(_cropY, 0);

        Canvas.SetLeft(_shadeBottom, 0);
        Canvas.SetTop(_shadeBottom, _cropY + _cropSide);
        _shadeBottom.Width = _displayWidth;
        _shadeBottom.Height = Math.Max(_displayHeight - (_cropY + _cropSide), 0);

        Canvas.SetLeft(_shadeLeft, 0);
        Canvas.SetTop(_shadeLeft, _cropY);
        _shadeLeft.Width = Math.Max(_cropX, 0);
        _shadeLeft.Height = _cropSide;

        Canvas.SetLeft(_shadeRight, _cropX + _cropSide);
        Canvas.SetTop(_shadeRight, _cropY);
        _shadeRight.Width = Math.Max(_displayWidth - (_cropX + _cropSide), 0);
        _shadeRight.Height = _cropSide;
    }

    // ---------- right column: live preview ----------

    private FrameworkElement BuildPreviewColumn()
    {
        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        column.Children.Add(new TextBlock
        {
            Text = "PREVIEW",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Faint,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10),
        });

        const double previewDisplay = ExportSize * 3.0;
        var frame = new Border
        {
            Width = previewDisplay,
            Height = previewDisplay,
            CornerRadius = new CornerRadius(8),
            Background = Theme.Ink,
            BorderBrush = Theme.Line2,
            BorderThickness = new Thickness(1),
        };
        _previewImage = new Image { Width = previewDisplay, Height = previewDisplay, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(_previewImage, BitmapScalingMode.NearestNeighbor);
        frame.Child = _previewImage;
        column.Children.Add(frame);

        column.Children.Add(new TextBlock
        {
            Text = $"{ExportSize} × {ExportSize} px — Stream Deck (15-key / MK.2)",
            FontSize = 11,
            Foreground = Theme.Faint,
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = previewDisplay,
        });
        return column;
    }

    // ---------- save section ----------

    private UIElement BuildSaveSection()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 22, 0, 0), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };

        var folderRow = new DockPanel { LastChildFill = true };
        var folderLabel = Theme.SmallLabel("Save to");
        DockPanel.SetDock(folderLabel, Dock.Left);
        folderRow.Children.Add(folderLabel);
        var chooseFolder = Theme.Btn("Choose folder…", primary: false, OnChooseFolderClicked);
        chooseFolder.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(chooseFolder, Dock.Right);
        folderRow.Children.Add(chooseFolder);
        _folderBox = new TextBox
        {
            Text = _destFolder,
            IsReadOnly = true,
            FontSize = 12,
            Background = Theme.Inset,
            Foreground = Theme.Dim,
            BorderBrush = Theme.Line2,
            Padding = new Thickness(8, 6, 8, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        folderRow.Children.Add(_folderBox);
        panel.Children.Add(Theme.InsetRow(folderRow));

        var nameRow = new DockPanel { LastChildFill = true };
        var nameLabel = Theme.SmallLabel("File name");
        DockPanel.SetDock(nameLabel, Dock.Left);
        nameRow.Children.Add(nameLabel);
        var suffix = new TextBlock { Text = ".png", FontSize = 12.5, Foreground = Theme.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(suffix, Dock.Right);
        nameRow.Children.Add(suffix);
        _nameBox = new TextBox
        {
            FontSize = 13,
            Background = Theme.Inset,
            Foreground = Theme.Text,
            CaretBrush = Theme.Text,
            BorderBrush = Theme.Line2,
            Padding = new Thickness(8, 6, 8, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        nameRow.Children.Add(_nameBox);
        panel.Children.Add(Theme.InsetRow(nameRow));

        var createRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0), LastChildFill = false };
        var createButton = Theme.Btn("✓  Create", primary: true, OnCreateClicked);
        DockPanel.SetDock(createButton, Dock.Left);
        createRow.Children.Add(createButton);
        panel.Children.Add(createRow);

        _saveStatus = new TextBlock { FontSize = 12, Foreground = Theme.Dim, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_saveStatus);

        return panel;
    }

    // ---------- image loading ----------

    private void SetupDragDrop(UIElement target)
    {
        target.AllowDrop = true;
        target.DragEnter += OnDragOver;
        target.DragOver += OnDragOver;
        target.DragLeave += (_, _) => SetDropHighlight(false);
        target.Drop += OnDrop;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var ok = FirstImagePath(e.Data) is not null;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropHighlight(ok);
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        if (FirstImagePath(e.Data) is { } path)
        {
            LoadImage(path);
        }
    }

    private static string? FirstImagePath(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return null;
        }

        string[] extensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];
        return files.FirstOrDefault(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
    }

    private void SetDropHighlight(bool on)
    {
        _dropZone.BorderBrush = on ? Theme.Accent : Theme.Line2;
        _dropZone.Background = on ? Theme.Card2 : Theme.Inset;
    }

    private void OnBrowseImageClicked()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose an image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff",
        };
        if (dialog.ShowDialog() == true)
        {
            LoadImage(dialog.FileName);
        }
    }

    private void LoadImage(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            SetSource(bitmap, Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception ex)
        {
            _saveStatus.Text = $"Couldn't open that image: {ex.Message}";
            _saveStatus.Foreground = Theme.Danger;
        }
    }

    private void SetSource(BitmapSource bitmap, string suggestedName)
    {
        _source = bitmap;
        var scale = Math.Min(CanvasSize / bitmap.PixelWidth, CanvasSize / bitmap.PixelHeight);
        _displayWidth = bitmap.PixelWidth * scale;
        _displayHeight = bitmap.PixelHeight * scale;

        _sourceImage.Source = bitmap;
        _sourceImage.Width = _displayWidth;
        _sourceImage.Height = _displayHeight;
        _cropCanvas.Width = _displayWidth;
        _cropCanvas.Height = _displayHeight;

        // default: the largest centered square that fits — a sensible center-crop out of the box
        _cropSide = Math.Min(_displayWidth, _displayHeight);
        _cropX = (_displayWidth - _cropSide) / 2;
        _cropY = (_displayHeight - _cropSide) / 2;
        ApplyCropVisual();

        _dropZone.Visibility = Visibility.Collapsed;
        _cropHost.Visibility = Visibility.Visible;
        _swapButton.Visibility = Visibility.Visible;
        _fileHint.Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight} px source";

        if (string.IsNullOrWhiteSpace(_nameBox.Text))
        {
            _nameBox.Text = SanitizeFileName(suggestedName);
        }

        _saveStatus.Text = "";
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        _previewImage.Source = ExportBitmap();
    }

    /// <summary>Crops the source at the current square (mapped from display to source pixels)
    /// and resamples it down to the export resolution.</summary>
    private BitmapSource? ExportBitmap()
    {
        if (_source is null || _cropSide <= 0)
        {
            return null;
        }

        var scale = _source.PixelWidth / _displayWidth;
        var srcSide = Math.Max(1, (int)Math.Round(_cropSide * scale));
        srcSide = Math.Min(srcSide, Math.Min(_source.PixelWidth, _source.PixelHeight));
        var srcX = Math.Clamp((int)Math.Round(_cropX * scale), 0, _source.PixelWidth - srcSide);
        var srcY = Math.Clamp((int)Math.Round(_cropY * scale), 0, _source.PixelHeight - srcSide);

        var cropped = new CroppedBitmap(_source, new Int32Rect(srcX, srcY, srcSide, srcSide));
        var resized = new TransformedBitmap(cropped,
            new ScaleTransform((double)ExportSize / srcSide, (double)ExportSize / srcSide));
        resized.Freeze();
        return resized;
    }

    // ---------- saving ----------

    private void OnChooseFolderClicked()
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Choose where to save button images",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_destFolder) ? _destFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            _destFolder = dialog.SelectedPath;
            _folderBox.Text = _destFolder;
        }
    }

    private void OnCreateClicked()
    {
        if (_source is null)
        {
            _saveStatus.Text = "Drop or choose an image first.";
            _saveStatus.Foreground = Theme.Warn;
            return;
        }

        if (_destFolder.Length == 0 || !Directory.Exists(_destFolder))
        {
            _saveStatus.Text = "Choose a destination folder first.";
            _saveStatus.Foreground = Theme.Warn;
            return;
        }

        var name = SanitizeFileName(_nameBox.Text.Trim());
        if (name.Length == 0)
        {
            _saveStatus.Text = "Give the file a name.";
            _saveStatus.Foreground = Theme.Warn;
            return;
        }

        var path = Path.Combine(_destFolder, name + ".png");
        if (File.Exists(path))
        {
            var choice = MessageBox.Show(
                $"\"{name}.png\" already exists in that folder. Overwrite it?",
                "Overwrite file?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes)
            {
                return;
            }
        }

        try
        {
            var bitmap = ExportBitmap()!;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            _saveStatus.Text = $"Saved {name}.png to {_destFolder}";
            _saveStatus.Foreground = Theme.Good;
        }
        catch (Exception ex)
        {
            _saveStatus.Text = $"Couldn't save: {ex.Message}";
            _saveStatus.Foreground = Theme.Danger;
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name;
    }
}

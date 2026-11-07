using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ShiftConvertApp;

/// <summary>
/// Интерактивное окно предпросмотра и покадрового выбора момента из видео для экспорта в PNG.
/// Поддерживает плавный скраббинг, воспроизведение (Пробел), шаг по 1 кадру (← / →), шаг по 1 сек и сохранение (Enter).
/// </summary>
public sealed class VideoFramePickerWindow : Window
{
    private readonly string _videoPath;
    private readonly Windows.Media.Editing.MediaComposition? _composition;
    private double _totalSeconds;
    private bool _isPlaying;
    private bool _isDraggingSlider;
    private bool _isFallbackMode;
    private bool _isSeekingFallback;

    private readonly DispatcherTimer _playTimer;
    private readonly MediaElement _mediaElement;
    private readonly Image _fallbackImage;
    private readonly Slider _slider;
    private readonly TextBlock _currentTimeText;
    private readonly TextBlock _totalTimeText;
    private readonly TextBlock _playIcon;
    private readonly Border _playOverlayBadge;

    public double SelectedSeconds { get; private set; }

    public VideoFramePickerWindow(
        string videoPath,
        Windows.Media.Editing.MediaComposition? composition = null,
        double totalSeconds = 0,
        int width = 0,
        int height = 0)
    {
        _videoPath = videoPath;
        _composition = composition;
        _totalSeconds = totalSeconds > 0 ? totalSeconds : 10.0;

        Title = Loc.T("Выбор кадра", "Select Frame", "Kare Seç");
        Width = 840;
        Height = 600;
        MinWidth = 660;
        MinHeight = 480;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = true;

        _playTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33) // ~30 fps обновление таймлайна
        };
        _playTimer.Tick += PlayTimer_Tick;

        // Корневая карточка со скруглением и мягкой тенью
        var rootBorder = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x17, 0x1C)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x31, 0x3A)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect
            {
                BlurRadius = 36,
                ShadowDepth = 10,
                Opacity = 0.55,
                Color = Colors.Black
            }
        };

        var mainGrid = new Grid();
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: Заголовок
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 1: Видео
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2: Таймлайн и кнопки
        rootBorder.Child = mainGrid;
        Content = rootBorder;

        // ==================== 0. Заголовок ====================
        var headerGrid = new Grid
        {
            Margin = new Thickness(20, 16, 16, 12),
            Background = Brushes.Transparent
        };
        headerGrid.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var iconText = new TextBlock
        {
            Text = "\uEB9F", // Символ кадра / фотоплёнки
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol"),
            FontSize = 17,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0xF2, 0x4E)), // Акцентный салатовый
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        titlePanel.Children.Add(iconText);

        var titleText = new TextBlock
        {
            Text = Loc.T("Выбор кадра PNG", "Select PNG Frame", "PNG Kare Seç"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        };
        titlePanel.Children.Add(titleText);

        string fileName = System.IO.Path.GetFileName(videoPath);
        string resInfo = width > 0 && height > 0 ? $" · {width}×{height}" : "";
        var subTitleText = new TextBlock
        {
            Text = $"  —  {fileName}{resInfo}",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9E, 0xA2, 0xB0)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 480
        };
        titlePanel.Children.Add(subTitleText);

        headerGrid.Children.Add(titlePanel);

        // Кнопка закрытия ✕
        var closeBtn = CreateIconButton("\uE711", 12, () =>
        {
            DialogResult = false;
            Close();
        });
        closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
        closeBtn.VerticalAlignment = VerticalAlignment.Center;
        headerGrid.Children.Add(closeBtn);

        Grid.SetRow(headerGrid, 0);
        mainGrid.Children.Add(headerGrid);

        // ==================== 1. Область видео ====================
        var videoContainer = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0D, 0x10)),
            CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x24, 0x2B)),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Margin = new Thickness(20, 0, 20, 14),
            Cursor = Cursors.Hand
        };
        videoContainer.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                ConfirmSelection();
            }
            else
            {
                TogglePlayPause();
            }
        };
        videoContainer.MouseWheel += (_, e) =>
        {
            double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 1.0 : 0.04;
            if (e.Delta > 0) SeekRelative(step);
            else if (e.Delta < 0) SeekRelative(-step);
            e.Handled = true;
        };

        var videoGrid = new Grid();

        _mediaElement = new MediaElement
        {
            LoadedBehavior = System.Windows.Controls.MediaState.Manual,
            UnloadedBehavior = System.Windows.Controls.MediaState.Manual,
            ScrubbingEnabled = true,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsMuted = true
        };
        _mediaElement.MediaOpened += MediaElement_MediaOpened;
        _mediaElement.MediaEnded += MediaElement_MediaEnded;
        _mediaElement.MediaFailed += MediaElement_MediaFailed;
        videoGrid.Children.Add(_mediaElement);

        _fallbackImage = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        videoGrid.Children.Add(_fallbackImage);

        // Полупрозрачный оверлей паузы по центру
        _playOverlayBadge = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(28),
            Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        var overlayIcon = new TextBlock
        {
            Text = "\uE768", // Play
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol"),
            FontSize = 22,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 0, 0)
        };
        _playOverlayBadge.Child = overlayIcon;
        videoGrid.Children.Add(_playOverlayBadge);

        videoContainer.Child = videoGrid;
        Grid.SetRow(videoContainer, 1);
        mainGrid.Children.Add(videoContainer);

        // ==================== 2. Таймлайн и элементы управления ====================
        var controlsPanel = new StackPanel
        {
            Margin = new Thickness(20, 0, 20, 18)
        };

        // Слайдер таймлайна
        _slider = new Slider
        {
            Minimum = 0,
            Maximum = _totalSeconds,
            Value = 0,
            Height = 26,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            Margin = new Thickness(0, 0, 0, 6)
        };
        _slider.PreviewMouseLeftButtonDown += Slider_PreviewMouseLeftButtonDown;
        _slider.PreviewMouseLeftButtonUp += Slider_PreviewMouseLeftButtonUp;
        _slider.ValueChanged += Slider_ValueChanged;
        controlsPanel.Children.Add(_slider);

        // Строка навигации: Время слева, Кнопки шага по центру, Подсказки справа
        var navGrid = new Grid { Margin = new Thickness(0, 2, 0, 14) };
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Время слева
        var timePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _currentTimeText = new TextBlock
        {
            Text = "00:00.00",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White
        };
        timePanel.Children.Add(_currentTimeText);

        var slashText = new TextBlock
        {
            Text = " / ",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80))
        };
        timePanel.Children.Add(slashText);

        _totalTimeText = new TextBlock
        {
            Text = FormatTime(_totalSeconds),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9E, 0xA2, 0xB0))
        };
        timePanel.Children.Add(_totalTimeText);
        Grid.SetColumn(timePanel, 0);
        navGrid.Children.Add(timePanel);

        // Кнопки покадрового шага по центру
        var stepButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var back1sBtn = CreateSmallButton("⏮ -1с", Loc.T("Назад на 1 сек (Shift+←)", "Back 1s (Shift+←)", "1 sn Geri (Shift+←)"), () => SeekRelative(-1.0));
        var backFrameBtn = CreateSmallButton("◀ -кадр", Loc.T("Назад на 1 кадр (←)", "Back 1 frame (←)", "1 kare Geri (←)"), () => SeekRelative(-0.04));

        var playBtn = new Border
        {
            Width = 40,
            Height = 32,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(0x27, 0x2A, 0x33)),
            Margin = new Thickness(5, 0, 5, 0),
            Cursor = Cursors.Hand,
            ToolTip = Loc.T("Воспроизведение / Пауза (Пробел)", "Play / Pause (Space)", "Oynat / Duraklat (Boşluk)")
        };
        _playIcon = new TextBlock
        {
            Text = "\uE768", // Play
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol"),
            FontSize = 15,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        playBtn.Child = _playIcon;
        playBtn.MouseEnter += (_, _) => playBtn.Background = new SolidColorBrush(Color.FromRgb(0x34, 0x38, 0x44));
        playBtn.MouseLeave += (_, _) => playBtn.Background = new SolidColorBrush(Color.FromRgb(0x27, 0x2A, 0x33));
        playBtn.MouseLeftButtonDown += (_, e) => { TogglePlayPause(); e.Handled = true; };

        var fwdFrameBtn = CreateSmallButton("+кадр ▶", Loc.T("Вперёд на 1 кадр (→)", "Forward 1 frame (→)", "1 kare İleri (→)"), () => SeekRelative(0.04));
        var fwd1sBtn = CreateSmallButton("+1с ⏭", Loc.T("Вперёд на 1 сек (Shift+→)", "Forward 1s (Shift+→)", "1 sn İleri (Shift+→)"), () => SeekRelative(1.0));

        stepButtons.Children.Add(back1sBtn);
        stepButtons.Children.Add(backFrameBtn);
        stepButtons.Children.Add(playBtn);
        stepButtons.Children.Add(fwdFrameBtn);
        stepButtons.Children.Add(fwd1sBtn);

        Grid.SetColumn(stepButtons, 1);
        navGrid.Children.Add(stepButtons);

        // Подсказка клавиатуры справа
        var hintText = new TextBlock
        {
            Text = Loc.T("Пробел: Старт/Пауза  ·  ← / →: Кадр  ·  Enter: ОК", "Space: Play  ·  ← / →: Frame  ·  Enter: OK", "Boşluk: Oynat  ·  ← / →: Kare  ·  Enter: Tamam"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(hintText, 2);
        navGrid.Children.Add(hintText);

        controlsPanel.Children.Add(navGrid);

        // Нижняя полоса действий: Инфо о PNG слева, Кнопки Отмена и Сохранить справа
        var bottomGrid = new Grid();
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var infoBadge = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var checkIcon = new TextBlock
        {
            Text = "\uE73E",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        var infoText = new TextBlock
        {
            Text = Loc.T("Оригинал PNG 100% · Копируется в буфер (Ctrl+V)", "Full-res PNG 100% · Copies to Clipboard (Ctrl+V)", "Orijinal PNG %100 · Panoya Kopyalanır (Ctrl+V)"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9E, 0xA2, 0xB0)),
            VerticalAlignment = VerticalAlignment.Center
        };
        infoBadge.Children.Add(checkIcon);
        infoBadge.Children.Add(infoText);
        Grid.SetColumn(infoBadge, 0);
        bottomGrid.Children.Add(infoBadge);

        var actionsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        var cancelBtn = CreateButton(
            Loc.T("Отмена", "Cancel", "İptal"),
            Color.FromRgb(0x27, 0x2A, 0x33),
            Color.FromRgb(0xD1, 0xD5, 0xDB),
            isAccent: false,
            onClick: () =>
            {
                DialogResult = false;
                Close();
            });
        cancelBtn.Margin = new Thickness(0, 0, 10, 0);
        actionsPanel.Children.Add(cancelBtn);

        var saveBtn = CreateButton(
            Loc.T("Сохранить кадр (Enter)", "Save Frame (Enter)", "Kareyi Kaydet (Enter)"),
            Color.FromRgb(0xC6, 0xF2, 0x4E), // Акцентный салатовый
            Color.FromRgb(0x11, 0x18, 0x27), // Тёмный текст для максимального контраста
            isAccent: true,
            onClick: ConfirmSelection);
        actionsPanel.Children.Add(saveBtn);

        Grid.SetColumn(actionsPanel, 1);
        bottomGrid.Children.Add(actionsPanel);

        controlsPanel.Children.Add(bottomGrid);

        Grid.SetRow(controlsPanel, 2);
        mainGrid.Children.Add(controlsPanel);

        // Горячие клавиши
        KeyDown += VideoFramePickerWindow_KeyDown;

        // Запуск загрузки видео
        try
        {
            _mediaElement.Source = new Uri(videoPath);
            _mediaElement.Play();
            _mediaElement.Pause();
        }
        catch
        {
            EnableFallbackMode();
        }
    }

    private void PlayTimer_Tick(object? sender, EventArgs e)
    {
        if (_isPlaying && !_isDraggingSlider)
        {
            double curSec = _mediaElement.Position.TotalSeconds;
            if (curSec >= _totalSeconds)
            {
                _isPlaying = false;
                _mediaElement.Pause();
                _playTimer.Stop();
                UpdatePlayIcon();
                curSec = _totalSeconds;
            }
            _slider.Value = curSec;
            _currentTimeText.Text = FormatTime(curSec);
        }
    }

    private void MediaElement_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (_mediaElement.NaturalDuration.HasTimeSpan)
        {
            double dur = _mediaElement.NaturalDuration.TimeSpan.TotalSeconds;
            if (dur > 0)
            {
                _totalSeconds = dur;
                _slider.Maximum = dur;
                _totalTimeText.Text = FormatTime(dur);
            }
        }

        // По умолчанию встаем на 15% длительности или 0.5с
        double initial = _totalSeconds > 1.2 ? Math.Min(_totalSeconds * 0.15, _totalSeconds - 0.1) : Math.Max(0.0, _totalSeconds * 0.5);
        _slider.Value = initial;
        SeekTo(initial);
    }

    private void MediaElement_MediaEnded(object sender, RoutedEventArgs e)
    {
        _isPlaying = false;
        _mediaElement.Pause();
        _playTimer.Stop();
        UpdatePlayIcon();
    }

    private void MediaElement_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        EnableFallbackMode();
    }

    private void EnableFallbackMode()
    {
        _isFallbackMode = true;
        _mediaElement.Visibility = Visibility.Collapsed;
        _fallbackImage.Visibility = Visibility.Visible;
        double initial = _totalSeconds > 1.2 ? Math.Min(_totalSeconds * 0.15, _totalSeconds - 0.1) : 0.0;
        _slider.Value = initial;
        _ = UpdateFallbackThumbnailAsync(initial);
    }

    private void Slider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingSlider = true;
        if (_isPlaying)
        {
            _isPlaying = false;
            _mediaElement.Pause();
            _playTimer.Stop();
            UpdatePlayIcon();
        }

        var p = e.GetPosition(_slider);
        if (_slider.ActualWidth > 0)
        {
            double ratio = Math.Clamp(p.X / _slider.ActualWidth, 0.0, 1.0);
            double newSec = _slider.Minimum + ratio * (_slider.Maximum - _slider.Minimum);
            _slider.Value = newSec;
            SeekTo(newSec);
        }
    }

    private void Slider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingSlider = false;
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isPlaying)
        {
            SeekTo(e.NewValue);
        }
    }

    private void SeekRelative(double deltaSec)
    {
        if (_isPlaying)
        {
            _isPlaying = false;
            _mediaElement.Pause();
            _playTimer.Stop();
            UpdatePlayIcon();
        }
        double target = Math.Clamp(_slider.Value + deltaSec, 0.0, _totalSeconds);
        _slider.Value = target;
        SeekTo(target);
    }

    private void SeekTo(double sec)
    {
        double clamped = Math.Clamp(sec, 0.0, _totalSeconds);
        _currentTimeText.Text = FormatTime(clamped);

        if (!_isFallbackMode)
        {
            _mediaElement.Position = TimeSpan.FromSeconds(clamped);
        }
        else
        {
            _ = UpdateFallbackThumbnailAsync(clamped);
        }
    }

    private async Task UpdateFallbackThumbnailAsync(double sec)
    {
        if (_composition == null || _isSeekingFallback) return;
        _isSeekingFallback = true;
        try
        {
            using var thumb = await _composition.GetThumbnailAsync(
                TimeSpan.FromSeconds(sec),
                640,
                0,
                Windows.Media.Editing.VideoFramePrecision.NearestFrame);

            using var netStream = thumb.AsStreamForRead();
            using var ms = new MemoryStream();
            await netStream.CopyToAsync(ms);
            ms.Position = 0;

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();

            _fallbackImage.Source = bi;
        }
        catch { }
        finally
        {
            _isSeekingFallback = false;
        }
    }

    private void TogglePlayPause()
    {
        if (_isFallbackMode) return;

        if (_isPlaying)
        {
            _isPlaying = false;
            _mediaElement.Pause();
            _playTimer.Stop();
            _playOverlayBadge.Visibility = Visibility.Visible;
        }
        else
        {
            if (_mediaElement.Position.TotalSeconds >= _totalSeconds - 0.1)
            {
                _mediaElement.Position = TimeSpan.Zero;
                _slider.Value = 0;
            }
            _isPlaying = true;
            _mediaElement.Play();
            _playTimer.Start();
            _playOverlayBadge.Visibility = Visibility.Collapsed;
        }
        UpdatePlayIcon();
    }

    private void UpdatePlayIcon()
    {
        _playIcon.Text = _isPlaying ? "\uE769" : "\uE768"; // Pause or Play
    }

    private void ConfirmSelection()
    {
        if (_isPlaying)
        {
            _isPlaying = false;
            _mediaElement.Pause();
            _playTimer.Stop();
        }
        SelectedSeconds = Math.Clamp(_slider.Value, 0.0, _totalSeconds);
        DialogResult = true;
        Close();
    }

    private void VideoFramePickerWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            ConfirmSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Space)
        {
            TogglePlayPause();
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 1.0 : 0.04;
            SeekRelative(-step);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 1.0 : 0.04;
            SeekRelative(step);
            e.Handled = true;
        }
        else if (e.Key == Key.Home)
        {
            SeekTo(0);
            _slider.Value = 0;
            e.Handled = true;
        }
        else if (e.Key == Key.End)
        {
            SeekTo(_totalSeconds);
            _slider.Value = _totalSeconds;
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _playTimer.Stop(); } catch { }
        try { _mediaElement.Stop(); } catch { }
        try { _mediaElement.Close(); } catch { }
        base.OnClosed(e);
    }

    private static string FormatTime(double sec)
    {
        if (sec < 0) sec = 0;
        int m = (int)(sec / 60);
        double s = sec - (m * 60);
        return $"{m:00}:{s:00.00}";
    }

    private static Border CreateSmallButton(string label, string tooltip, Action onClick)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x24, 0x2B)),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(3, 0, 3, 0),
            Cursor = Cursors.Hand,
            ToolTip = tooltip
        };
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB))
        };
        border.Child = text;
        border.MouseEnter += (_, _) => border.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x32, 0x3D));
        border.MouseLeave += (_, _) => border.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x24, 0x2B));
        border.MouseLeftButtonDown += (_, e) => { onClick(); e.Handled = true; };
        return border;
    }

    private static Border CreateIconButton(string iconGlyph, double fontSize, Action onClick)
    {
        var border = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand
        };
        var icon = new TextBlock
        {
            Text = iconGlyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol"),
            FontSize = fontSize,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9E, 0xA2, 0xB0)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        border.Child = icon;
        border.MouseEnter += (_, _) =>
        {
            border.Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
            icon.Foreground = Brushes.White;
        };
        border.MouseLeave += (_, _) =>
        {
            border.Background = Brushes.Transparent;
            icon.Foreground = new SolidColorBrush(Color.FromRgb(0x9E, 0xA2, 0xB0));
        };
        border.MouseLeftButtonDown += (_, e) => { onClick(); e.Handled = true; };
        return border;
    }

    private static Border CreateButton(string label, Color bg, Color fg, bool isAccent, Action onClick)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(bg),
            Padding = new Thickness(isAccent ? 18 : 14, 8, isAccent ? 18 : 14, 8),
            Cursor = Cursors.Hand
        };
        var text = new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontWeight = isAccent ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = new SolidColorBrush(fg),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        border.Child = text;

        Color hoverBg = isAccent
            ? Color.FromArgb(255, (byte)Math.Min(255, bg.R + 25), (byte)Math.Min(255, bg.G + 25), (byte)Math.Min(255, bg.B + 25))
            : Color.FromArgb(255, 0x36, 0x3A, 0x46);

        border.MouseEnter += (_, _) => border.Background = new SolidColorBrush(hoverBg);
        border.MouseLeave += (_, _) => border.Background = new SolidColorBrush(bg);
        border.MouseLeftButtonDown += (_, e) => { onClick(); e.Handled = true; };
        return border;
    }
}

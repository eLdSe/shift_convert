using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ShiftConvertApp;

/// <summary>
/// Премиальное окно предпросмотра и покадрового выбора момента из видео для экспорта в PNG.
/// Оснащено кастомным плавным скраббером с hover-таймкодом, переключением звука (M),
/// пошаговой перемоткой по 1 кадру (← / →), шагом по 1 сек и мгновенным сохранением (Enter).
/// </summary>
public sealed class VideoFramePickerWindow : Window
{
    private static readonly SolidColorBrush AccentLime = Freeze(new SolidColorBrush(Color.FromRgb(0xC6, 0xF2, 0x4E)));
    private static readonly SolidColorBrush TrackBg = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x37)));
    private static readonly SolidColorBrush TrackHoverBg = Freeze(new SolidColorBrush(Color.FromRgb(0x38, 0x3C, 0x49)));
    private static readonly SolidColorBrush BubbleBg = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x20, 0x28)));
    private static readonly SolidColorBrush BubbleBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x4A)));

    private static SolidColorBrush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    private readonly string _videoPath;
    private readonly Windows.Media.Editing.MediaComposition? _composition;
    private double _totalSeconds;
    private double _currentSeconds;
    private bool _isPlaying;
    private bool _isDraggingScrubber;
    private bool _isFallbackMode;
    private bool _isSeekingFallback;

    private readonly DispatcherTimer _playTimer;
    private readonly MediaElement _mediaElement;
    private readonly Image _fallbackImage;

    // Кастомный скраббер
    private readonly Grid _scrubberContainer;
    private readonly Border _scrubberTrack;
    private readonly Border _scrubberProgress;
    private readonly Border _scrubberThumb;
    private readonly Border _hoverBubble;
    private readonly TextBlock _hoverBubbleText;

    private readonly TextBlock _currentTimeText;
    private readonly TextBlock _totalTimeText;
    private readonly TextBlock _playIcon;
    private readonly TextBlock _muteIcon;
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
        Width = 860;
        Height = 620;
        MinWidth = 680;
        MinHeight = 500;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = true;

        _playTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33) // ~30 fps плавное обновление таймлайна
        };
        _playTimer.Tick += PlayTimer_Tick;

        // Корневая карточка True-Black-Flat со скруглением и глубокой тенью
        var rootBorder = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromRgb(0x13, 0x14, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x27, 0x2A, 0x33)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect
            {
                BlurRadius = 40,
                ShadowDepth = 12,
                Opacity = 0.60,
                Color = Colors.Black
            }
        };

        var mainGrid = new Grid();
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: Шапка
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 1: Видеохолст
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2: Таймлайн и действия
        rootBorder.Child = mainGrid;
        Content = rootBorder;

        // ==================== 0. Шапка ====================
        var headerGrid = new Grid
        {
            Margin = new Thickness(22, 16, 16, 12),
            Background = Brushes.Transparent
        };
        headerGrid.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var iconText = new TextBlock
        {
            Text = "\uEB9F", // Иконка кадра
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol"),
            FontSize = 17,
            Foreground = AccentLime,
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

        // ==================== 1. Видеохолст ====================
        var videoContainer = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0D)),
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x22, 0x2A)),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Margin = new Thickness(20, 0, 20, 12),
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
            IsMuted = true // По умолчанию без звука для бесшумного скраббинга
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

        // Оверлей воспроизведения/паузы по центру
        _playOverlayBadge = new Border
        {
            Width = 60,
            Height = 60,
            CornerRadius = new CornerRadius(30),
            Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
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
            FontSize = 24,
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

        // ==================== 2. Панель таймлайна и действий ====================
        var controlsPanel = new StackPanel
        {
            Margin = new Thickness(20, 0, 20, 18)
        };

        // --- Кастомный скраббер в стиле Telegram/QuickTime ---
        _scrubberContainer = new Grid
        {
            Height = 32,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent, // Ловит события мыши по всей высоте
            Margin = new Thickness(0, 0, 0, 6)
        };

        // Фоновый трек
        _scrubberTrack = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = TrackBg,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _scrubberContainer.Children.Add(_scrubberTrack);

        // Заполнение прогресса
        _scrubberProgress = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = AccentLime,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0
        };
        _scrubberContainer.Children.Add(_scrubberProgress);

        // Ползунок (Thumb)
        _scrubberThumb = new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(6),
            Background = Brushes.White,
            BorderBrush = AccentLime,
            BorderThickness = new Thickness(1.5),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(-6, 0, 0, 0),
            Effect = new DropShadowEffect
            {
                BlurRadius = 8,
                ShadowDepth = 1,
                Opacity = 0.45,
                Color = Colors.Black
            }
        };
        _scrubberContainer.Children.Add(_scrubberThumb);

        // Всплывающий таймкод при наведении (Hover Tooltip Bubble)
        _hoverBubble = new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = BubbleBg,
            BorderBrush = BubbleBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 3, 7, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                BlurRadius = 10,
                ShadowDepth = 3,
                Opacity = 0.5,
                Color = Colors.Black
            }
        };
        _hoverBubbleText = new TextBlock
        {
            Text = "00:00.00",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White
        };
        _hoverBubble.Child = _hoverBubbleText;
        _scrubberContainer.Children.Add(_hoverBubble);

        // События скраббера
        _scrubberContainer.MouseEnter += Scrubber_MouseEnter;
        _scrubberContainer.MouseLeave += Scrubber_MouseLeave;
        _scrubberContainer.MouseMove += Scrubber_MouseMove;
        _scrubberContainer.MouseLeftButtonDown += Scrubber_MouseLeftButtonDown;
        _scrubberContainer.MouseLeftButtonUp += Scrubber_MouseLeftButtonUp;
        _scrubberContainer.SizeChanged += (_, _) => UpdateScrubberVisuals();

        controlsPanel.Children.Add(_scrubberContainer);

        // --- Строка навигации: Таймкод слева, Кнопки по центру, Клавиши справа ---
        var navGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Таймкод слева
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

        // Кнопки по центру (Шаг -1с, -кадр, Play/Pause, +кадр, +1с, Звук)
        var stepButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var back1sBtn = CreateSmallButton("⏮ -1с", Loc.T("Назад 1 сек (Shift+←)", "Back 1s (Shift+←)", "1 sn Geri (Shift+←)"), () => SeekRelative(-1.0));
        var backFrameBtn = CreateSmallButton("◀ -кадр", Loc.T("Назад 1 кадр (←)", "Back 1 frame (←)", "1 kare Geri (←)"), () => SeekRelative(-0.04));

        var playBtn = new Border
        {
            Width = 44,
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
        playBtn.MouseEnter += (_, _) => playBtn.Background = new SolidColorBrush(Color.FromRgb(0x35, 0x39, 0x46));
        playBtn.MouseLeave += (_, _) => playBtn.Background = new SolidColorBrush(Color.FromRgb(0x27, 0x2A, 0x33));
        playBtn.MouseLeftButtonDown += (_, e) => { TogglePlayPause(); e.Handled = true; };

        var fwdFrameBtn = CreateSmallButton("+кадр ▶", Loc.T("Вперёд 1 кадр (→)", "Forward 1 frame (→)", "1 kare İleri (→)"), () => SeekRelative(0.04));
        var fwd1sBtn = CreateSmallButton("+1с ⏭", Loc.T("Вперёд 1 сек (Shift+→)", "Forward 1s (Shift+→)", "1 sn İleri (Shift+→)"), () => SeekRelative(1.0));

        // Кнопка включения/выключения звука (Mute / Unmute)
        var muteBtn = new Border
        {
            Width = 34,
            Height = 32,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x22, 0x2A)),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = Loc.T("Звук (M)", "Audio Toggle (M)", "Ses (M)")
        };
        _muteIcon = new TextBlock
        {
            Text = "\uE74F", // Mute icon
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol"),
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9E, 0xA2, 0xB0)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        muteBtn.Child = _muteIcon;
        muteBtn.MouseEnter += (_, _) => muteBtn.Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x30, 0x3B));
        muteBtn.MouseLeave += (_, _) => muteBtn.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x22, 0x2A));
        muteBtn.MouseLeftButtonDown += (_, e) => { ToggleMute(); e.Handled = true; };

        stepButtons.Children.Add(back1sBtn);
        stepButtons.Children.Add(backFrameBtn);
        stepButtons.Children.Add(playBtn);
        stepButtons.Children.Add(fwdFrameBtn);
        stepButtons.Children.Add(fwd1sBtn);
        stepButtons.Children.Add(muteBtn);

        Grid.SetColumn(stepButtons, 1);
        navGrid.Children.Add(stepButtons);

        // Подсказка горячих клавиш справа
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

        // --- Нижняя полоса действий ---
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
            Color.FromRgb(0x24, 0x26, 0x30),
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
            Color.FromRgb(0xC6, 0xF2, 0x4E),
            Color.FromRgb(0x11, 0x18, 0x27),
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

    // --- Обработка скраббера ---
    private void Scrubber_MouseEnter(object sender, MouseEventArgs e)
    {
        _scrubberTrack.Height = 6;
        _scrubberProgress.Height = 6;
        _scrubberThumb.Width = 16;
        _scrubberThumb.Height = 16;
        _scrubberThumb.CornerRadius = new CornerRadius(8);
        _scrubberThumb.Margin = new Thickness(-8, 0, 0, 0);
    }

    private void Scrubber_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isDraggingScrubber)
        {
            _scrubberTrack.Height = 4;
            _scrubberProgress.Height = 4;
            _scrubberThumb.Width = 12;
            _scrubberThumb.Height = 12;
            _scrubberThumb.CornerRadius = new CornerRadius(6);
            _scrubberThumb.Margin = new Thickness(-6, 0, 0, 0);
            _hoverBubble.Visibility = Visibility.Collapsed;
        }
    }

    private void Scrubber_MouseMove(object sender, MouseEventArgs e)
    {
        double width = _scrubberContainer.ActualWidth;
        if (width <= 0 || _totalSeconds <= 0) return;

        Point pt = e.GetPosition(_scrubberContainer);
        double ratio = Math.Clamp(pt.X / width, 0.0, 1.0);
        double hoverSec = ratio * _totalSeconds;

        // Позиционируем hover bubble точно над курсором
        _hoverBubbleText.Text = FormatTime(hoverSec);
        double bubbleX = Math.Clamp(pt.X - 28, 0, Math.Max(0, width - 60));
        _hoverBubble.Margin = new Thickness(bubbleX, -14, 0, 0);
        _hoverBubble.Visibility = Visibility.Visible;

        if (_isDraggingScrubber)
        {
            SeekTo(hoverSec);
        }
    }

    private void Scrubber_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingScrubber = true;
        _scrubberContainer.CaptureMouse();
        if (_isPlaying)
        {
            _isPlaying = false;
            _mediaElement.Pause();
            _playTimer.Stop();
            UpdatePlayIcon();
        }

        double width = _scrubberContainer.ActualWidth;
        if (width > 0 && _totalSeconds > 0)
        {
            Point pt = e.GetPosition(_scrubberContainer);
            double ratio = Math.Clamp(pt.X / width, 0.0, 1.0);
            SeekTo(ratio * _totalSeconds);
        }
    }

    private void Scrubber_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingScrubber = false;
        _scrubberContainer.ReleaseMouseCapture();
        if (!_scrubberContainer.IsMouseOver)
        {
            Scrubber_MouseLeave(sender, e);
        }
    }

    private void UpdateScrubberVisuals()
    {
        double width = _scrubberContainer.ActualWidth;
        if (width <= 0 || _totalSeconds <= 0) return;

        double ratio = Math.Clamp(_currentSeconds / _totalSeconds, 0.0, 1.0);
        double progressWidth = ratio * width;
        _scrubberProgress.Width = progressWidth;
        _scrubberThumb.Margin = new Thickness(progressWidth - (_scrubberThumb.Width / 2.0), 0, 0, 0);
    }

    private void PlayTimer_Tick(object? sender, EventArgs e)
    {
        if (_isPlaying && !_isDraggingScrubber)
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
            _currentSeconds = curSec;
            _currentTimeText.Text = FormatTime(curSec);
            UpdateScrubberVisuals();
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
                _totalTimeText.Text = FormatTime(dur);
            }
        }

        double initial = _totalSeconds > 1.2 ? Math.Min(_totalSeconds * 0.15, _totalSeconds - 0.1) : Math.Max(0.0, _totalSeconds * 0.5);
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
        SeekTo(initial);
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
        SeekTo(Math.Clamp(_currentSeconds + deltaSec, 0.0, _totalSeconds));
    }

    private void SeekTo(double sec)
    {
        _currentSeconds = Math.Clamp(sec, 0.0, _totalSeconds);
        _currentTimeText.Text = FormatTime(_currentSeconds);
        UpdateScrubberVisuals();

        if (!_isFallbackMode)
        {
            _mediaElement.Position = TimeSpan.FromSeconds(_currentSeconds);
        }
        else
        {
            _ = UpdateFallbackThumbnailAsync(_currentSeconds);
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
            }
            _isPlaying = true;
            _mediaElement.Play();
            _playTimer.Start();
            _playOverlayBadge.Visibility = Visibility.Collapsed;
        }
        UpdatePlayIcon();
    }

    private void ToggleMute()
    {
        _mediaElement.IsMuted = !_mediaElement.IsMuted;
        if (_mediaElement.IsMuted)
        {
            _muteIcon.Text = "\uE74F"; // Mute
            _muteIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x9E, 0xA2, 0xB0));
        }
        else
        {
            _muteIcon.Text = "\uE767"; // Unmuted
            _muteIcon.Foreground = AccentLime;
        }
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
        SelectedSeconds = Math.Clamp(_currentSeconds, 0.0, _totalSeconds);
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
        else if (e.Key == Key.M)
        {
            ToggleMute();
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
            e.Handled = true;
        }
        else if (e.Key == Key.End)
        {
            SeekTo(_totalSeconds);
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
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x22, 0x2A)),
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
        border.MouseEnter += (_, _) => border.Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x30, 0x3B));
        border.MouseLeave += (_, _) => border.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x22, 0x2A));
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
            : Color.FromArgb(255, 0x33, 0x36, 0x42);

        border.MouseEnter += (_, _) => border.Background = new SolidColorBrush(hoverBg);
        border.MouseLeave += (_, _) => border.Background = new SolidColorBrush(bg);
        border.MouseLeftButtonDown += (_, e) => { onClick(); e.Handled = true; };
        return border;
    }
}

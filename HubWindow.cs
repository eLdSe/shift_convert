using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace ShiftConvertApp;

/// <summary>
/// Главное окно ShiftConvert в эстетике True-Black-Flat (Ruled T-Frame):
/// - Единый фон #000000 у сайдбара, шапки и холста, разделённый линиями 1px #1A1A1A.
/// - Карточки #111111 (CornerRadius 20) без теней и свечений, вложенные пилюли #2A2A2A.
/// - Инвертированная белая основная кнопка (#FFFFFF фон, #000000 текст).
/// - Акцентный лайм #C6F24E строго для статусов, активных индикаторов и метрик.
/// - Крестик (✕) и Alt+F4 сворачивают окно в фоновый режим (0% CPU), а кнопка «Выключить» полностью завершает процесс.
/// </summary>
public sealed class HubWindow : Window
{
    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 20;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractAssociatedIcon(IntPtr hInst, string lpIconPath, ref ushort lpiIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    // Палитра True-Black-Flat
    private static readonly SolidColorBrush BgGround = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00)));
    private static readonly SolidColorBrush BgSurface = Freeze(new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)));
    private static readonly SolidColorBrush BgNavActive = Freeze(new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16)));
    private static readonly SolidColorBrush BgRaised = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)));
    private static readonly SolidColorBrush BgRaisedHover = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)));
    private static readonly SolidColorBrush RuleHairline = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)));
    private static readonly SolidColorBrush BorderOuter = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)));
    private static readonly SolidColorBrush BorderInteractive = Freeze(new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x38)));

    private static readonly SolidColorBrush TextPrimary = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
    private static readonly SolidColorBrush TextMuted = Freeze(new SolidColorBrush(Color.FromRgb(0xB4, 0xB4, 0xB4)));
    private static readonly SolidColorBrush TextFaint = Freeze(new SolidColorBrush(Color.FromRgb(0x94, 0x94, 0x94)));

    private static readonly SolidColorBrush AccentLime = Freeze(new SolidColorBrush(Color.FromRgb(0xC6, 0xF2, 0x4E)));
    private static readonly SolidColorBrush AccentLimeSoft = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x10)));
    private static readonly SolidColorBrush DangerCoral = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x56, 0x6F)));
    private static readonly SolidColorBrush DangerSoft = Freeze(new SolidColorBrush(Color.FromRgb(0x2C, 0x1A, 0x20)));

    private static readonly FontFamily IconFont = new("Segoe MDL2 Assets");

    private static SolidColorBrush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    private IntPtr _hwnd;
    private bool _isExplicitExit;
    private int _activeTab; // 0 = Обзор, 1 = Настройки

    // Сайдбар
    private readonly TextBlock _brandScopeText;
    private readonly Border _navOverviewBtn;
    private readonly TextBlock _navOverviewLabel;
    private readonly Border _navSettingsBtn;
    private readonly TextBlock _navSettingsLabel;
    private readonly Ellipse _bgStatusDot;
    private readonly TextBlock _bgStatusText;
    private readonly TextBlock _quitSidebarLabel;

    // Топ-бар
    private readonly TextBlock _topTitleText;
    private readonly TextBlock _topFindingText;
    private readonly TextBlock _langBtnLabel;
    private readonly Border _closeBtnBorder;

    // Вкладка 0: Обзор (KPI, Drop-карточка, Таблица истории)
    private readonly Grid _overviewView;
    private readonly TextBlock _kpiConvertedLabel;
    private readonly TextBlock _kpiConvertedValue;
    private readonly TextBlock _kpiConvertedSub;
    private readonly TextBlock _kpiSavedLabel;
    private readonly TextBlock _kpiSavedValue;
    private readonly TextBlock _kpiSavedSub;
    private readonly TextBlock _kpiHookLabel;
    private readonly TextBlock _kpiHookValue;
    private readonly TextBlock _kpiHookSub;

    private readonly Border _dropSheetBorder;
    private readonly TextBlock _dropTitleText;
    private readonly TextBlock _dropSubText;
    private readonly TextBlock _browseBtnLabel;
    private readonly Border _clearSelectionBtn;
    private readonly TextBlock _clearSelectionLabel;
    private readonly WrapPanel _formatPillsPanel;
    private readonly StackPanel _inlineProgressPanel;
    private readonly TextBlock _inlineProgressText;
    private readonly Border _inlineProgressFill;
    private readonly Border _cancelConvertBtn;
    private readonly TextBlock _cancelConvertLabel;

    private readonly TextBlock _historySectionTitle;
    private readonly Border _clearHistoryBtn;
    private readonly TextBlock _clearHistoryLabel;
    private readonly TextBlock _colFileHeader;
    private readonly TextBlock _colFormatHeader;
    private readonly TextBlock _colSizeHeader;
    private readonly TextBlock _colTimeHeader;
    private readonly TextBlock _colActionHeader;
    private readonly StackPanel _historyRowsPanel;

    // Вкладка 1: Настройки
    private readonly StackPanel _settingsView;
    private readonly TextBlock _setShiftTitle = null!;
    private readonly TextBlock _setShiftDesc = null!;
    private readonly Border _setShiftSwitch = null!;
    private readonly Border _setShiftKnob = null!;

    private readonly TextBlock _setCtxTitle = null!;
    private readonly TextBlock _setCtxDesc = null!;
    private readonly Border _setCtxSwitch = null!;
    private readonly Border _setCtxKnob = null!;

    private readonly TextBlock _setClipTitle = null!;
    private readonly TextBlock _setClipDesc = null!;
    private readonly Border _setClipSwitch = null!;
    private readonly Border _setClipKnob = null!;

    private readonly TextBlock _setToastTitle = null!;
    private readonly TextBlock _setToastDesc = null!;
    private readonly Border _setToastSwitch = null!;
    private readonly Border _setToastKnob = null!;

    private readonly TextBlock _setAutoTitle = null!;
    private readonly TextBlock _setAutoDesc = null!;
    private readonly Border _setAutoSwitch = null!;
    private readonly Border _setAutoKnob = null!;

    private readonly TextBlock _powerCardTitle;
    private readonly TextBlock _powerCardDesc;
    private readonly TextBlock _hideToBgBtnLabel;
    private readonly TextBlock _quitFullBtnLabel;

    private string[] _selectedFiles = Array.Empty<string>();

    public HubWindow()
    {
        Title = "ShiftConvert";
        Width = 780;
        Height = 540;
        try
        {
            Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/app.ico"));
        }
        catch { }
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AllowDrop = true;

        // Корневой фрейм: чистый #000000 с тонкой 1px рамкой #222222
        var outerFrame = new Border
        {
            CornerRadius = new CornerRadius(20),
            Background = BgGround,
            BorderBrush = BorderOuter,
            BorderThickness = new Thickness(1),
            ClipToBounds = true
        };

        // Ruled T-Frame: левая колонка (196px) + 1px линия + правая рабочая область
        var shellGrid = new Grid();
        shellGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(196) });
        shellGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        shellGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ==================== ЛЕВЫЙ САЙДБАР ====================
        var sidebarGrid = new Grid { Background = BgGround, Margin = new Thickness(16, 18, 16, 16) };
        sidebarGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: Бренд
        sidebarGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 1: Навигация
        sidebarGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2: Фоновый статус + Выключить

        // Бренд-блок с векторной монограммой
        var brandRow = new Grid { Margin = new Thickness(4, 0, 0, 22), Background = Brushes.Transparent };
        brandRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        brandRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        brandRow.MouseLeftButtonDown += OnDragWindow;

        var markBorder = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(9),
            Background = BgNavActive,
            BorderBrush = BorderOuter,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 9,2 L 3,10 L 8,10 L 7,16 L 13,8 L 8,8 Z"),
                Fill = AccentLime,
                Stretch = Stretch.Uniform,
                Width = 13,
                Height = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Grid.SetColumn(markBorder, 0);

        var brandTextStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        brandTextStack.Children.Add(new TextBlock
        {
            Text = "ShiftConvert",
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimary
        });
        _brandScopeText = new TextBlock
        {
            FontSize = 11,
            Foreground = TextFaint
        };
        brandTextStack.Children.Add(_brandScopeText);
        Grid.SetColumn(brandTextStack, 1);

        brandRow.Children.Add(markBorder);
        brandRow.Children.Add(brandTextStack);
        Grid.SetRow(brandRow, 0);
        sidebarGrid.Children.Add(brandRow);

        // Навигация (шаг 36px, только 2 пункта верхнего уровня)
        var navStack = new StackPanel();
        (_navOverviewBtn, _navOverviewLabel) = CreateNavRow("\uE80F", () => SwitchTab(0));
        (_navSettingsBtn, _navSettingsLabel) = CreateNavRow("\uE713", () => SwitchTab(1));
        navStack.Children.Add(_navOverviewBtn);
        navStack.Children.Add(_navSettingsBtn);
        Grid.SetRow(navStack, 1);
        sidebarGrid.Children.Add(navStack);

        // Нижний блок сайдбара: индикатор работы в фоне + кнопка полного выключения
        var bottomSidebarStack = new StackPanel();

        var statusPill = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = BgSurface,
            BorderBrush = RuleHairline,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _bgStatusDot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = AccentLime,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _bgStatusText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = TextMuted,
            VerticalAlignment = VerticalAlignment.Center
        };
        statusRow.Children.Add(_bgStatusDot);
        statusRow.Children.Add(_bgStatusText);
        statusPill.Child = statusRow;
        bottomSidebarStack.Children.Add(statusPill);

        // Явная кнопка полного завершения процесса («Выключить приложение»)
        var quitSidebarBtn = new Border
        {
            Height = 34,
            CornerRadius = new CornerRadius(10),
            Background = DangerSoft,
            BorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x48, 0x22, 0x2B))),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 0, 10, 0),
            Cursor = Cursors.Hand
        };
        var quitRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        quitRow.Children.Add(new TextBlock
        {
            Text = "\uE7E8",
            FontFamily = IconFont,
            FontSize = 12,
            Foreground = DangerCoral,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        });
        _quitSidebarLabel = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = DangerCoral,
            VerticalAlignment = VerticalAlignment.Center
        };
        quitRow.Children.Add(_quitSidebarLabel);
        quitSidebarBtn.Child = quitRow;
        quitSidebarBtn.MouseEnter += (_, _) => quitSidebarBtn.Background = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x20, 0x28)));
        quitSidebarBtn.MouseLeave += (_, _) => quitSidebarBtn.Background = DangerSoft;
        quitSidebarBtn.MouseLeftButtonUp += (_, _) => QuitApp();
        bottomSidebarStack.Children.Add(quitSidebarBtn);

        Grid.SetRow(bottomSidebarStack, 2);
        sidebarGrid.Children.Add(bottomSidebarStack);

        Grid.SetColumn(sidebarGrid, 0);
        shellGrid.Children.Add(sidebarGrid);

        // Вертикальная разделительная линия 1px #1A1A1A
        var vRule = new Border { Background = RuleHairline };
        Grid.SetColumn(vRule, 1);
        shellGrid.Children.Add(vRule);

        // ==================== ПРАВАЯ ОБЛАСТЬ (ШАПКА + ХОЛСТ) ====================
        var rightGrid = new Grid { Background = BgGround };
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(58) }); // 0: Топ-бар
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });  // 1: Горизонтальная линия 1px
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 2: Контент

        // Топ-бар
        var topBarGrid = new Grid { Background = BgGround, Margin = new Thickness(24, 0, 18, 0) };
        topBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var topTitleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _topTitleText = new TextBlock
        {
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center
        };
        _topFindingText = new TextBlock
        {
            FontSize = 12,
            Foreground = TextFaint,
            Margin = new Thickness(12, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        topTitleStack.Children.Add(_topTitleText);
        topTitleStack.Children.Add(_topFindingText);

        var dragTitleArea = new Border
        {
            Background = Brushes.Transparent,
            Child = topTitleStack
        };
        dragTitleArea.MouseLeftButtonDown += OnDragWindow;
        Grid.SetColumn(dragTitleArea, 0);

        var topControlsRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        // Переключатель языка (RU / EN / TR)
        var langPill = CreateTopIconPill(out _langBtnLabel, () =>
        {
            AppConfig.Data.Lang = AppConfig.Data.Lang switch
            {
                "ru" => "en",
                "en" => "tr",
                _ => "ru"
            };
            AppConfig.Save();
            RefreshLocalization();
        }, width: 38, isIconFont: false);

        // Кнопка сворачивания
        var minPill = CreateTopIconPill(out var minIcon, HideToTray, width: 32, isIconFont: true);
        minIcon.Text = "\uE921";

        // Кнопка крестика (✕) — скрывает окно в фон, оставляя перехват Shift работать!
        _closeBtnBorder = CreateTopIconPill(out var closeIcon, HideToTray, width: 32, isIconFont: true);
        closeIcon.Text = "\uE8BB";

        topControlsRow.Children.Add(langPill);
        topControlsRow.Children.Add(minPill);
        topControlsRow.Children.Add(_closeBtnBorder);
        Grid.SetColumn(topControlsRow, 1);

        topBarGrid.Children.Add(dragTitleArea);
        topBarGrid.Children.Add(topControlsRow);
        Grid.SetRow(topBarGrid, 0);
        rightGrid.Children.Add(topBarGrid);

        // Горизонтальная линия 1px #1A1A1A
        var hRule = new Border { Background = RuleHairline };
        Grid.SetRow(hRule, 1);
        rightGrid.Children.Add(hRule);

        // Контейнер экранов
        var contentHost = new Grid { Margin = new Thickness(24, 18, 24, 20) };

        // -------------------- ЭКРАН 0: ОБЗОР --------------------
        _overviewView = new Grid();
        _overviewView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: Divided KPI Strip
        _overviewView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1: Drop & Convert Card
        _overviewView.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 2: Recent Table

        // 0. Divided KPI Strip (одна поверхность #111111, разделённая 1px линиями #1A1A1A на 3 колонки)
        var kpiStrip = new Border
        {
            CornerRadius = new CornerRadius(20),
            Background = BgSurface,
            BorderBrush = RuleHairline,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 14)
        };
        var kpiGrid = new Grid();
        kpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        kpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        kpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        kpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        kpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var kpiCol0 = CreateKpiCell(out _kpiConvertedLabel, out _kpiConvertedValue, out _kpiConvertedSub);
        Grid.SetColumn(kpiCol0, 0);
        kpiGrid.Children.Add(kpiCol0);

        var kpiRule1 = new Border { Background = RuleHairline, Margin = new Thickness(0, 14, 0, 14) };
        Grid.SetColumn(kpiRule1, 1);
        kpiGrid.Children.Add(kpiRule1);

        var kpiCol1 = CreateKpiCell(out _kpiSavedLabel, out _kpiSavedValue, out _kpiSavedSub);
        Grid.SetColumn(kpiCol1, 2);
        kpiGrid.Children.Add(kpiCol1);

        var kpiRule2 = new Border { Background = RuleHairline, Margin = new Thickness(0, 14, 0, 14) };
        Grid.SetColumn(kpiRule2, 3);
        kpiGrid.Children.Add(kpiRule2);

        var kpiCol2 = CreateKpiCell(out _kpiHookLabel, out _kpiHookValue, out _kpiHookSub);
        Grid.SetColumn(kpiCol2, 4);
        kpiGrid.Children.Add(kpiCol2);

        kpiStrip.Child = kpiGrid;
        Grid.SetRow(kpiStrip, 0);
        _overviewView.Children.Add(kpiStrip);

        // 1. Карточка приёма файлов и быстрого выбора формата (#111111, радиус 20px, инвертированная белая кнопка)
        _dropSheetBorder = new Border
        {
            CornerRadius = new CornerRadius(20),
            Background = BgSurface,
            BorderBrush = RuleHairline,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 16, 20, 16),
            Margin = new Thickness(0, 0, 0, 14),
            AllowDrop = true
        };

        var dropOuterStack = new StackPanel();
        var dropTopRow = new Grid();
        dropTopRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dropTopRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dropTextStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        _dropTitleText = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _dropSubText = new TextBlock
        {
            FontSize = 12,
            Foreground = TextFaint,
            Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        dropTextStack.Children.Add(_dropTitleText);
        dropTextStack.Children.Add(_dropSubText);
        Grid.SetColumn(dropTextStack, 0);

        var dropButtonsRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        // Вторичная кнопка сброса выбранных файлов
        _clearSelectionBtn = new Border
        {
            Height = 36,
            CornerRadius = new CornerRadius(18),
            Background = BgRaised,
            BorderBrush = BorderInteractive,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed
        };
        _clearSelectionLabel = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            Foreground = TextMuted,
            VerticalAlignment = VerticalAlignment.Center
        };
        _clearSelectionBtn.Child = _clearSelectionLabel;
        _clearSelectionBtn.MouseEnter += (_, _) => _clearSelectionBtn.Background = BgRaisedHover;
        _clearSelectionBtn.MouseLeave += (_, _) => _clearSelectionBtn.Background = BgRaised;
        _clearSelectionBtn.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _selectedFiles = Array.Empty<string>();
            _formatPillsPanel.Visibility = Visibility.Collapsed;
            _clearSelectionBtn.Visibility = Visibility.Collapsed;
            RefreshLocalization();
        };
        dropButtonsRow.Children.Add(_clearSelectionBtn);

        // Основная кнопка по правилу инверсии (Solid #FFFFFF pill, #000000 label)
        var browsePrimaryBtn = new Border
        {
            Height = 36,
            CornerRadius = new CornerRadius(18),
            Background = TextPrimary,
            Padding = new Thickness(18, 0, 18, 0),
            Cursor = Cursors.Hand
        };
        _browseBtnLabel = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = BgGround,
            VerticalAlignment = VerticalAlignment.Center
        };
        browsePrimaryBtn.Child = _browseBtnLabel;
        browsePrimaryBtn.MouseEnter += (_, _) => browsePrimaryBtn.Background = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)));
        browsePrimaryBtn.MouseLeave += (_, _) => browsePrimaryBtn.Background = TextPrimary;
        browsePrimaryBtn.MouseLeftButtonUp += OnDropZoneClick;
        dropButtonsRow.Children.Add(browsePrimaryBtn);

        Grid.SetColumn(dropButtonsRow, 1);
        dropTopRow.Children.Add(dropTextStack);
        dropTopRow.Children.Add(dropButtonsRow);
        dropOuterStack.Children.Add(dropTopRow);

        _formatPillsPanel = new WrapPanel
        {
            Margin = new Thickness(0, 12, 0, 0),
            Visibility = Visibility.Collapsed
        };
        dropOuterStack.Children.Add(_formatPillsPanel);

        _inlineProgressPanel = new StackPanel
        {
            Margin = new Thickness(0, 12, 0, 0),
            Visibility = Visibility.Collapsed
        };

        var progressTopRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        progressTopRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        progressTopRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _inlineProgressText = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = AccentLime,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 12, 0)
        };
        Grid.SetColumn(_inlineProgressText, 0);
        progressTopRow.Children.Add(_inlineProgressText);

        var cancelInlineRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var cancelInlineIcon = new TextBlock
        {
            Text = "\uE711",
            FontFamily = IconFont,
            FontSize = 9.5,
            Foreground = DangerCoral,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        };
        _cancelConvertLabel = new TextBlock
        {
            Text = Loc.T("Отменить", "Cancel", "İptal"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = DangerCoral,
            VerticalAlignment = VerticalAlignment.Center
        };
        cancelInlineRow.Children.Add(cancelInlineIcon);
        cancelInlineRow.Children.Add(_cancelConvertLabel);

        _cancelConvertBtn = new Border
        {
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = DangerSoft,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x24, 0x2D)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(11, 0, 11, 0),
            Cursor = Cursors.Hand,
            ToolTip = Loc.T("Остановить конвертацию и удалить неполный файл", "Stop conversion and remove partial file", "Dönüştürmeyi durdur ve eksik dosyayı sil"),
            Child = cancelInlineRow
        };
        _cancelConvertBtn.MouseEnter += (_, _) =>
        {
            _cancelConvertBtn.Background = new SolidColorBrush(Color.FromRgb(0x36, 0x1A, 0x22));
            _cancelConvertBtn.BorderBrush = DangerCoral;
        };
        _cancelConvertBtn.MouseLeave += (_, _) =>
        {
            _cancelConvertBtn.Background = DangerSoft;
            _cancelConvertBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x24, 0x2D));
        };
        _cancelConvertBtn.MouseLeftButtonUp += (_, ev) =>
        {
            ev.Handled = true;
            RadialWheelWindow.CancelActiveConversion();
        };
        Grid.SetColumn(_cancelConvertBtn, 1);
        progressTopRow.Children.Add(_cancelConvertBtn);

        _inlineProgressFill = new Border
        {
            Height = 4,
            Width = 36,
            CornerRadius = new CornerRadius(2),
            Background = AccentLime,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var inlineTrack = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = RuleHairline,
            Child = _inlineProgressFill
        };
        _inlineProgressPanel.Children.Add(progressTopRow);
        _inlineProgressPanel.Children.Add(inlineTrack);
        dropOuterStack.Children.Add(_inlineProgressPanel);

        _dropSheetBorder.Child = dropOuterStack;
        _dropSheetBorder.DragOver += OnWindowDragOver;
        _dropSheetBorder.Drop += OnWindowDrop;
        Grid.SetRow(_dropSheetBorder, 1);
        _overviewView.Children.Add(_dropSheetBorder);

        // 2. Таблица недавних конвертаций (#111111, радиус 20px, тонкие линии 1px #1A1A1A, tabular-nums)
        var tableCard = new Border
        {
            CornerRadius = new CornerRadius(20),
            Background = BgSurface,
            BorderBrush = RuleHairline,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 14, 20, 12)
        };
        var tableGrid = new Grid();
        tableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Заголовок секции
        tableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Шапка колонок
        tableGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) }); // Линия
        tableGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Строки

        var historyHeaderBar = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        historyHeaderBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        historyHeaderBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _historySectionTitle = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_historySectionTitle, 0);
        historyHeaderBar.Children.Add(_historySectionTitle);

        _clearHistoryLabel = new TextBlock
        {
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = TextMuted,
            VerticalAlignment = VerticalAlignment.Center
        };
        _clearHistoryBtn = new Border
        {
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = BgRaised,
            BorderBrush = BorderOuter,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 0, 12, 0),
            Cursor = Cursors.Hand,
            Child = _clearHistoryLabel
        };
        _clearHistoryBtn.MouseEnter += (_, _) =>
        {
            _clearHistoryBtn.Background = DangerSoft;
            _clearHistoryLabel.Foreground = DangerCoral;
        };
        _clearHistoryBtn.MouseLeave += (_, _) =>
        {
            _clearHistoryBtn.Background = BgRaised;
            _clearHistoryLabel.Foreground = TextMuted;
        };
        _clearHistoryBtn.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            AppConfig.ClearHistory();
            RefreshHistoryUI();
        };
        Grid.SetColumn(_clearHistoryBtn, 1);
        historyHeaderBar.Children.Add(_clearHistoryBtn);

        Grid.SetRow(historyHeaderBar, 0);
        tableGrid.Children.Add(historyHeaderBar);

        var colHeaderGrid = CreateTableGridRow();
        colHeaderGrid.Margin = new Thickness(0, 0, 0, 7);
        _colFileHeader = CreateColumnHeaderLabel();
        _colFormatHeader = CreateColumnHeaderLabel();
        _colSizeHeader = CreateColumnHeaderLabel(TextAlignment.Right);
        _colTimeHeader = CreateColumnHeaderLabel(TextAlignment.Right);
        _colActionHeader = CreateColumnHeaderLabel(TextAlignment.Right);

        Grid.SetColumn(_colFileHeader, 0);
        Grid.SetColumn(_colFormatHeader, 1);
        Grid.SetColumn(_colSizeHeader, 2);
        Grid.SetColumn(_colTimeHeader, 3);
        Grid.SetColumn(_colActionHeader, 4);

        colHeaderGrid.Children.Add(_colFileHeader);
        colHeaderGrid.Children.Add(_colFormatHeader);
        colHeaderGrid.Children.Add(_colSizeHeader);
        colHeaderGrid.Children.Add(_colTimeHeader);
        colHeaderGrid.Children.Add(_colActionHeader);
        Grid.SetRow(colHeaderGrid, 1);
        tableGrid.Children.Add(colHeaderGrid);

        var headerRule = new Border { Background = RuleHairline };
        Grid.SetRow(headerRule, 2);
        tableGrid.Children.Add(headerRule);

        var historyScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        _historyRowsPanel = new StackPanel();
        historyScroll.Content = _historyRowsPanel;
        Grid.SetRow(historyScroll, 3);
        tableGrid.Children.Add(historyScroll);

        tableCard.Child = tableGrid;
        Grid.SetRow(tableCard, 2);
        _overviewView.Children.Add(tableCard);

        contentHost.Children.Add(_overviewView);

        // -------------------- ЭКРАН 1: НАСТРОЙКИ --------------------
        _settingsView = new StackPanel { Visibility = Visibility.Collapsed };

        var settingsCard = new Border
        {
            CornerRadius = new CornerRadius(20),
            Background = BgSurface,
            BorderBrush = RuleHairline,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 6, 20, 6),
            Margin = new Thickness(0, 0, 0, 14)
        };
        var settingsRowsStack = new StackPanel();

        settingsRowsStack.Children.Add(CreateSettingRow(
            out _setShiftTitle, out _setShiftDesc, out _setShiftSwitch, out _setShiftKnob,
            () =>
            {
                AppConfig.Data.HoldShiftEnabled = !AppConfig.Data.HoldShiftEnabled;
                AppConfig.Save();
                RefreshAllStateUI();
            }));
        settingsRowsStack.Children.Add(new Border { Height = 1, Background = RuleHairline });

        settingsRowsStack.Children.Add(CreateSettingRow(
            out _setCtxTitle, out _setCtxDesc, out _setCtxSwitch, out _setCtxKnob,
            () =>
            {
                bool next = !AppConfig.IsContextMenuEnabled();
                AppConfig.Data.ContextMenuEnabled = next;
                AppConfig.SetContextMenu(next);
                AppConfig.Save();
                RefreshAllStateUI();
            }));
        settingsRowsStack.Children.Add(new Border { Height = 1, Background = RuleHairline });

        settingsRowsStack.Children.Add(CreateSettingRow(
            out _setClipTitle, out _setClipDesc, out _setClipSwitch, out _setClipKnob,
            () =>
            {
                AppConfig.Data.AutoCopyClipboard = !AppConfig.Data.AutoCopyClipboard;
                AppConfig.Save();
                RefreshAllStateUI();
            }));
        settingsRowsStack.Children.Add(new Border { Height = 1, Background = RuleHairline });

        settingsRowsStack.Children.Add(CreateSettingRow(
            out _setToastTitle, out _setToastDesc, out _setToastSwitch, out _setToastKnob,
            () =>
            {
                AppConfig.Data.ShowToast = !AppConfig.Data.ShowToast;
                AppConfig.Save();
                RefreshAllStateUI();
            }));
        settingsRowsStack.Children.Add(new Border { Height = 1, Background = RuleHairline });

        settingsRowsStack.Children.Add(CreateSettingRow(
            out _setAutoTitle, out _setAutoDesc, out _setAutoSwitch, out _setAutoKnob,
            () =>
            {
                AppConfig.SetAutoStart(!AppConfig.IsAutoStartEnabled());
                RefreshAllStateUI();
            }));

        settingsCard.Child = settingsRowsStack;
        _settingsView.Children.Add(settingsCard);

        // Карточка управления фоновым процессом и выключением
        var powerCard = new Border
        {
            CornerRadius = new CornerRadius(20),
            Background = BgSurface,
            BorderBrush = RuleHairline,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 16, 20, 16)
        };
        var powerGrid = new Grid();
        powerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        powerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var powerTextStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        _powerCardTitle = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimary
        };
        _powerCardDesc = new TextBlock
        {
            FontSize = 12,
            Foreground = TextFaint,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        powerTextStack.Children.Add(_powerCardTitle);
        powerTextStack.Children.Add(_powerCardDesc);
        Grid.SetColumn(powerTextStack, 0);

        var powerBtnsRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var hideToBgBtn = new Border
        {
            Height = 36,
            CornerRadius = new CornerRadius(18),
            Background = BgRaised,
            BorderBrush = BorderInteractive,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand
        };
        _hideToBgBtnLabel = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center
        };
        hideToBgBtn.Child = _hideToBgBtnLabel;
        hideToBgBtn.MouseEnter += (_, _) => hideToBgBtn.Background = BgRaisedHover;
        hideToBgBtn.MouseLeave += (_, _) => hideToBgBtn.Background = BgRaised;
        hideToBgBtn.MouseLeftButtonUp += (_, _) => HideToTray();
        powerBtnsRow.Children.Add(hideToBgBtn);

        var quitFullBtn = new Border
        {
            Height = 36,
            CornerRadius = new CornerRadius(18),
            Background = DangerSoft,
            BorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x48, 0x22, 0x2B))),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 0, 16, 0),
            Cursor = Cursors.Hand
        };
        _quitFullBtnLabel = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = DangerCoral,
            VerticalAlignment = VerticalAlignment.Center
        };
        quitFullBtn.Child = _quitFullBtnLabel;
        quitFullBtn.MouseEnter += (_, _) => quitFullBtn.Background = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x20, 0x28)));
        quitFullBtn.MouseLeave += (_, _) => quitFullBtn.Background = DangerSoft;
        quitFullBtn.MouseLeftButtonUp += (_, _) => QuitApp();
        powerBtnsRow.Children.Add(quitFullBtn);

        Grid.SetColumn(powerBtnsRow, 1);
        powerGrid.Children.Add(powerTextStack);
        powerGrid.Children.Add(powerBtnsRow);
        powerCard.Child = powerGrid;
        _settingsView.Children.Add(powerCard);

        contentHost.Children.Add(_settingsView);

        Grid.SetRow(contentHost, 2);
        rightGrid.Children.Add(contentHost);

        Grid.SetColumn(rightGrid, 2);
        shellGrid.Children.Add(rightGrid);

        outerFrame.Child = shellGrid;
        Content = outerFrame;

        DragOver += OnWindowDragOver;
        Drop += OnWindowDrop;

        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();

        SwitchTab(0);
        RefreshLocalization();
    }

    private void OnDragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    /// <summary>
    /// Перехватываем системное закрытие (Alt+F4 / закрытие с панели задач):
    /// если не нажата явная кнопка «Выключить», окно полностью выгружается из памяти в спящий режим.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExplicitExit)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        base.OnClosing(e);
    }

    public void ForceCloseForSleep()
    {
        _isExplicitExit = true;
        try { Close(); } catch { }
    }

    /// <summary>
    /// Полное выключение фонового процесса ShiftConvert.
    /// </summary>
    public void QuitApp()
    {
        App.QuitApplication();
    }

    private void SwitchTab(int tabIndex)
    {
        _activeTab = tabIndex;
        _overviewView.Visibility = tabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        _settingsView.Visibility = tabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;

        _navOverviewBtn.Background = tabIndex == 0 ? BgNavActive : Brushes.Transparent;
        _navOverviewLabel.Foreground = tabIndex == 0 ? TextPrimary : TextMuted;
        _navOverviewLabel.FontWeight = tabIndex == 0 ? FontWeights.SemiBold : FontWeights.Normal;

        _navSettingsBtn.Background = tabIndex == 1 ? BgNavActive : Brushes.Transparent;
        _navSettingsLabel.Foreground = tabIndex == 1 ? TextPrimary : TextMuted;
        _navSettingsLabel.FontWeight = tabIndex == 1 ? FontWeights.SemiBold : FontWeights.Normal;

        UpdateTopBarTitles();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
    }

    public void DisposeTray()
    {
        if (Application.Current is App app)
        {
            app.DisposeTray();
        }
    }

    public void SetConvertingState(bool isConverting, IReadOnlyList<string> files, ConvertOption opt)
    {
        if (isConverting)
        {
            _inlineProgressPanel.Visibility = Visibility.Visible;
            string nameInfo = files.Count == 1
                ? System.IO.Path.GetFileName(files[0])
                : $"{files.Count} {Loc.T("файл(ов)", "files", "dosya")}";
            _inlineProgressText.Text = Loc.T(
                $"Выполняется: {opt.Label} ({nameInfo})…",
                $"Processing: {opt.Label} ({nameInfo})…",
                $"İşleniyor: {opt.Label} ({nameInfo})…");
            var anim = new DoubleAnimation(24, 460, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            _inlineProgressFill.BeginAnimation(WidthProperty, anim);
        }
        else
        {
            _inlineProgressFill.BeginAnimation(WidthProperty, null);
            _inlineProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    public void UpdateProgress(double pct, IReadOnlyList<string> files, ConvertOption opt)
    {
        int clamped = Math.Clamp((int)Math.Round(pct), 1, 99);
        string nameInfo = files.Count == 1
            ? System.IO.Path.GetFileName(files[0])
            : $"{files.Count} {Loc.T("файл(ов)", "files", "dosya")}";
        _inlineProgressText.Text = Loc.T(
            $"Выполняется: {opt.Label} — {clamped}% ({nameInfo})",
            $"Processing: {opt.Label} — {clamped}% ({nameInfo})",
            $"İşleniyor: {opt.Label} — %{clamped} ({nameInfo})");
        _inlineProgressFill.BeginAnimation(WidthProperty, null);
        _inlineProgressFill.Width = Math.Clamp(490.0 * (clamped / 100.0), 24.0, 490.0);
    }

    public void ShowHub()
    {
        RefreshLocalization();
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void HideToTray()
    {
        App.UnloadHubToSleepMode();
    }

    private void OnDropZoneClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("Выберите файлы для конвертации", "Select files to convert", "Dönüştürülecek dosyaları seçin"),
            Multiselect = true,
            Filter = Loc.T("Все файлы (*.*)|*.*", "All Files (*.*)|*.*", "Tüm Dosyalar (*.*)|*.*")
        };
        if (dlg.ShowDialog() == true && dlg.FileNames.Length > 0)
        {
            if (_activeTab != 0) SwitchTab(0);
            ShowSelectedFilesOptions(dlg.FileNames);
        }
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            _dropSheetBorder.BorderBrush = AccentLime;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        _dropSheetBorder.BorderBrush = RuleHairline;

        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Length > 0)
        {
            if (_activeTab != 0) SwitchTab(0);
            ShowSelectedFilesOptions(files);
        }
        e.Handled = true;
    }

    private void ShowSelectedFilesOptions(string[] files)
    {
        _selectedFiles = files;
        _dropTitleText.Text = files.Length == 1
            ? System.IO.Path.GetFileName(files[0])
            : $"{files.Length} {Loc.T("файл(ов) готово к конвертации", "files ready to convert", "dosya dönüştürmeye hazır")}";
        _dropSubText.Text = Loc.T(
            "Нажмите на нужный целевой формат ниже — результат сразу скопируется в буфер (Ctrl+V)",
            "Click a target format below — output is automatically copied to clipboard (Ctrl+V)",
            "Aşağıdan hedef formatı seçin — sonuç otomatik olarak panoya kopyalanır (Ctrl+V)");

        _clearSelectionBtn.Visibility = Visibility.Visible;
        _formatPillsPanel.Children.Clear();
        _formatPillsPanel.Visibility = Visibility.Visible;

        var options = ConvertEngine.GetOptionsForFiles(files, forWheel: false);
        foreach (var opt in options)
        {
            var pill = new Border
            {
                Height = 32,
                CornerRadius = new CornerRadius(16),
                Background = BgRaised,
                BorderBrush = BorderInteractive,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = Cursors.Hand,
                ToolTip = opt.SubLabel
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var iconTb = new TextBlock
            {
                Text = opt.Icon,
                FontFamily = IconFont,
                FontSize = 12,
                Foreground = new SolidColorBrush(opt.Accent),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            var labelTb = new TextBlock
            {
                Text = opt.Label,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(iconTb);
            row.Children.Add(labelTb);
            pill.Child = row;

            pill.MouseEnter += (_, _) =>
            {
                pill.Background = BgRaisedHover;
                pill.BorderBrush = new SolidColorBrush(opt.Accent);
            };
            pill.MouseLeave += (_, _) =>
            {
                pill.Background = BgRaised;
                pill.BorderBrush = BorderInteractive;
            };
            pill.MouseLeftButtonUp += (_, ev) =>
            {
                ev.Handled = true;
                RadialWheelWindow.ExecuteAndNotify(files, opt);
            };
            _formatPillsPanel.Children.Add(pill);
        }
    }

    private void UpdateTopBarTitles()
    {
        if (_activeTab == 0)
        {
            _topTitleText.Text = Loc.T("Обзор", "Overview", "Genel Bakış");
            _topFindingText.Text = Loc.T(
                "Потяните любой файл в Проводнике и нажмите Shift",
                "Drag any file in Explorer and press Shift",
                "Dosyayı sürükleyin ve Shift tuşuna basın");
        }
        else
        {
            _topTitleText.Text = Loc.T("Настройки", "Settings", "Ayarlar");
            _topFindingText.Text = Loc.T(
                "Параметры фонового перехвата и поведения буфера обмена",
                "Background hook and clipboard behavior preferences",
                "Arka plan yakalama ve pano davranış ayarları");
        }
    }

    public void RefreshLocalization()
    {
        _langBtnLabel.Text = AppConfig.Data.Lang.ToUpperInvariant();
        _brandScopeText.Text = Loc.T("Локальный движок", "Local engine", "Yerel motor");
        _navOverviewLabel.Text = Loc.T("Обзор", "Overview", "Genel Bakış");
        _navSettingsLabel.Text = Loc.T("Настройки", "Settings", "Ayarlar");
        _quitSidebarLabel.Text = Loc.T("Выключить приложение", "Quit ShiftConvert", "Uygulamayı Kapat");

        _closeBtnBorder.ToolTip = Loc.T(
            "Свернуть в фон (ShiftConvert продолжит работать)",
            "Hide to background (ShiftConvert keeps running)",
            "Arka plana gizle (ShiftConvert çalışmaya devam eder)");

        UpdateTopBarTitles();

        _browseBtnLabel.Text = Loc.T("Выбрать файлы", "Select files", "Dosya seç");
        _clearSelectionLabel.Text = Loc.T("Сбросить", "Clear", "Temizle");
        _cancelConvertLabel.Text = Loc.T("Отменить", "Cancel", "İptal");

        if (_selectedFiles.Length == 0)
        {
            _dropTitleText.Text = Loc.T(
                "Перетащите файлы сюда или выберите на диске",
                "Drop files here or select from disk",
                "Dosyaları buraya bırakın veya diskten seçin");
            _dropSubText.Text = "MP4 · MP3 · GIF · PNG · JPG · WEBP · PDF · OCR · ICO · ZIP · CSV ↔ JSON";
        }
        else
        {
            ShowSelectedFilesOptions(_selectedFiles);
        }

        _historySectionTitle.Text = Loc.T("Недавние конвертации (макс. 5)", "Recent conversions (max 5)", "Son dönüştürmeler (maks. 5)");
        _clearHistoryLabel.Text = Loc.T("Очистить все", "Clear all", "Tümünü temizle");
        _colFileHeader.Text = Loc.T("Файл", "File", "Dosya");
        _colFormatHeader.Text = Loc.T("Формат", "Format", "Format");
        _colSizeHeader.Text = Loc.T("Размер", "Size", "Boyut");
        _colTimeHeader.Text = Loc.T("Время", "Time", "Saat");
        _colActionHeader.Text = Loc.T("Папка", "Folder", "Klasör");

        _setShiftTitle.Text = Loc.T("Радиальное колесо при зажатии Shift", "Radial wheel on Shift key", "Shift tuşuyla radyal tekerlek");
        _setShiftDesc.Text = Loc.T(
            "Открывать круговое меню форматов при перетаскивании файла с клавишей Shift (в спящем режиме ~3 МБ ОЗУ)",
            "Open the radial format selector when dragging any file + Shift (sleep mode ~3 MB RAM)",
            "Dosyayı sürüklerken Shift'e basıldığında dairesel menüyü aç (~3 MB RAM)");

        _setCtxTitle.Text = Loc.T("Меню правой кнопки мыши (ПКМ в Проводнике)", "Explorer right-click context menu", "Dosya Gezgini sağ tık menüsü");
        _setCtxDesc.Text = Loc.T(
            "Пункт «⚡ ShiftConvert» при клике ПКМ по файлу или папке — работает даже когда приложение полностью выключено",
            "Add '⚡ ShiftConvert' to right-click menu — works on-demand even when background process is quit",
            "Sağ tık menüsüne '⚡ ShiftConvert' ekle — uygulama tamamen kapalıyken bile çalışır");

        _setClipTitle.Text = Loc.T("Автокопирование результата в буфер (Ctrl+V)", "Auto-copy converted file to clipboard (Ctrl+V)", "Sonucu otomatik panoya kopyala (Ctrl+V)");
        _setClipDesc.Text = Loc.T(
            "Мгновенно помещать готовый файл или изображение в буфер обмена Windows после конвертации",
            "Immediately place the converted file or bitmap into the Windows clipboard",
            "Dönüştürülen dosyayı hemen Windows panosuna yerleştir");

        _setToastTitle.Text = Loc.T("Всплывающее уведомление сверху экрана", "Top-edge completion notification", "Ekran üstü bildirim kapsülü");
        _setToastDesc.Text = Loc.T(
            "Показывать компактную капсулу на 3 секунды после завершения конвертации",
            "Show a compact pill notification for 3 seconds after conversion completes",
            "Dönüştürme tamamlandığında 3 saniyelik bildirim göster");

        _setAutoTitle.Text = Loc.T("Запускать в спящем режиме вместе с Windows", "Start in sleep mode with Windows", "Windows ile uyku modunda başlat");
        _setAutoDesc.Text = Loc.T(
            "Запускать только микро-слушатель Shift без создания окна (0% CPU, ~3 МБ ОЗУ)",
            "Start only the micro Shift listener without creating UI window (0% CPU, ~3 MB RAM)",
            "Pencere oluşturmadan yalnızca mikro Shift dinleyicisini başlat (%0 CPU, ~3 MB RAM)");

        _powerCardTitle.Text = Loc.T("Спящий режим и полное выключение", "Sleep mode & full shutdown", "Uyku modu ve tamamen kapatma");
        _powerCardDesc.Text = Loc.T(
            "Крестик (✕) полностью выгружает окно из памяти в спящий режим (работает Shift). «Выключить» завершает процесс (остаётся работать меню ПКМ).",
            "Close (✕) unloads the window into sleep mode (Shift works). 'Quit' terminates the process (Right-Click menu still works).",
            "Kapat (✕) pencereyi bellekten kaldırır (Shift çalışır). 'Kapat' süreci sonlandırır (Sağ tık menüsü çalışmaya devam eder).");
        _hideToBgBtnLabel.Text = Loc.T("В спящий режим", "Sleep mode", "Uyku modu");
        _quitFullBtnLabel.Text = Loc.T("Выключить полностью", "Quit process", "Tamamen kapat");

        RefreshAllStateUI();
        RefreshHistoryUI();
    }

    private void RefreshAllStateUI()
    {
        bool shiftOn = AppConfig.Data.HoldShiftEnabled;
        _bgStatusDot.Fill = shiftOn ? AccentLime : TextFaint;
        _bgStatusText.Text = shiftOn
            ? Loc.T("Спящий хук · 0% CPU", "Sleep hook · 0% CPU", "Uyku kancası · %0 CPU")
            : Loc.T("Только ПКМ", "Right-click only", "Sadece sağ tık");

        int totalCount = Math.Max(AppConfig.Data.TotalConvertedCount, AppConfig.Data.History.Count);
        _kpiConvertedLabel.Text = Loc.T("Сконвертировано файлов", "Files converted", "Dönüştürülen dosya");
        _kpiConvertedValue.Text = totalCount.ToString("N0");
        _kpiConvertedSub.Text = Loc.T("Локально без облака", "100% local processing", "Tamamen yerel");

        _kpiSavedLabel.Text = Loc.T("Сэкономлено места", "Storage saved", "Kazanılan alan");
        _kpiSavedValue.Text = ConvertEngine.FormatBytes(AppConfig.Data.TotalSavedBytes);
        _kpiSavedValue.Foreground = AppConfig.Data.TotalSavedBytes > 0 ? AccentLime : TextPrimary;
        _kpiSavedSub.Text = Loc.T("Сжатие и WebP/ZIP", "Compression & WebP/ZIP", "Sıkıştırma & WebP/ZIP");

        _kpiHookLabel.Text = Loc.T("Режим вызова", "Trigger mode", "Tetikleme modu");
        _kpiHookValue.Text = shiftOn
            ? Loc.T("Shift + ПКМ", "Shift + Menu", "Shift + Menü")
            : Loc.T("Только ПКМ", "Right-Click", "Sağ Tık");
        _kpiHookValue.Foreground = shiftOn ? AccentLime : TextPrimary;
        _kpiHookSub.Text = Loc.T("Окно выгружается в фоне", "UI unloads when closed", "Pencere bellekte tutulmaz");

        UpdateSwitchVisual(_setShiftSwitch, _setShiftKnob, shiftOn);
        UpdateSwitchVisual(_setCtxSwitch, _setCtxKnob, AppConfig.IsContextMenuEnabled());
        UpdateSwitchVisual(_setClipSwitch, _setClipKnob, AppConfig.Data.AutoCopyClipboard);
        UpdateSwitchVisual(_setToastSwitch, _setToastKnob, AppConfig.Data.ShowToast);
        UpdateSwitchVisual(_setAutoSwitch, _setAutoKnob, AppConfig.IsAutoStartEnabled());
    }

    public void RefreshHistoryUI()
    {
        RefreshAllStateUI();
        _historyRowsPanel.Children.Clear();
        _clearHistoryBtn.Visibility = AppConfig.Data.History.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (AppConfig.Data.History.Count == 0)
        {
            var emptyStack = new StackPanel
            {
                Margin = new Thickness(0, 28, 0, 20),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            emptyStack.Children.Add(new TextBlock
            {
                Text = Loc.T(
                    "Список недавних конвертаций пуст",
                    "No converted files yet",
                    "Henüz dönüştürülen dosya yok"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextMuted,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            emptyStack.Children.Add(new TextBlock
            {
                Text = Loc.T(
                    "Потяните любой файл мышью в Проводнике и нажмите клавишу Shift, либо выберите файл выше.",
                    "Drag any file in File Explorer and press Shift, or select a file above.",
                    "Dosya Gezgini'nde bir dosyayı sürükleyip Shift'e basın veya yukarıdan seçin."),
                FontSize = 12,
                Foreground = TextFaint,
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            _historyRowsPanel.Children.Add(emptyStack);
            return;
        }

        for (int i = 0; i < AppConfig.Data.History.Count; i++)
        {
            var item = AppConfig.Data.History[i];

            var rowContainer = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(0, 8, 0, 8),
                Cursor = Cursors.Hand
            };

            var g = CreateTableGridRow();

            // 0: Имя файла
            var fileNameTb = new TextBlock
            {
                Text = item.FileName,
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                Foreground = TextPrimary,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 12, 0)
            };
            Grid.SetColumn(fileNameTb, 0);

            // 1: Статус-пилюля формата (#222A10 фон, #C6F24E текст)
            var formatBadge = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = AccentLimeSoft,
                Padding = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = item.Format,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = AccentLime
                }
            };
            Grid.SetColumn(formatBadge, 1);

            // 2: Размер (tabular-nums)
            var sizeTb = new TextBlock
            {
                Text = string.IsNullOrEmpty(item.SizeInfo) ? "—" : item.SizeInfo,
                FontSize = 12,
                Foreground = TextMuted,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right
            };
            Typography.SetNumeralAlignment(sizeTb, FontNumeralAlignment.Tabular);
            Grid.SetColumn(sizeTb, 2);

            // 3: Время (tabular-nums)
            var timeTb = new TextBlock
            {
                Text = item.TimeText,
                FontSize = 12,
                Foreground = TextFaint,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right
            };
            Typography.SetNumeralAlignment(timeTb, FontNumeralAlignment.Tabular);
            Grid.SetColumn(timeTb, 3);

            // 4: Действие («Открыть»)
            var actionTb = new TextBlock
            {
                Text = Loc.T("Открыть", "Reveal", "Göster"),
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = TextMuted,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right
            };
            Grid.SetColumn(actionTb, 4);

            // 5: Кнопки удаления: Корзина (удалить сам файл с диска) + Крестик (убрать из списка)
            var actionsRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Кнопка корзины — удаляет созданный файл с диска и убирает строку
            var trashIcon = new TextBlock
            {
                Text = "\uE74D",
                FontFamily = IconFont,
                FontSize = 11,
                Foreground = TextFaint,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var trashBtn = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = Brushes.Transparent,
                Margin = new Thickness(0, 0, 2, 0),
                ToolTip = Loc.T("Удалить сконвертированный файл с диска", "Delete converted file from disk", "Dönüştürülen dosyayı diskten sil"),
                Child = trashIcon
            };
            trashBtn.MouseEnter += (_, _) =>
            {
                trashBtn.Background = DangerSoft;
                trashIcon.Foreground = DangerCoral;
            };
            trashBtn.MouseLeave += (_, _) =>
            {
                trashBtn.Background = Brushes.Transparent;
                trashIcon.Foreground = TextFaint;
            };
            trashBtn.MouseLeftButtonUp += (_, ev) =>
            {
                ev.Handled = true;
                AppConfig.RemoveHistoryItem(item, deleteFileFromDisk: true);
                RefreshHistoryUI();
            };

            // Кнопка крестика — убирает запись только из списка (ПКМ — тоже удаляет файл)
            var delIcon = new TextBlock
            {
                Text = "\uE711",
                FontFamily = IconFont,
                FontSize = 10,
                Foreground = TextFaint,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var delBtn = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = Brushes.Transparent,
                ToolTip = Loc.T("Убрать из списка (файл останется в папке)", "Remove from list (keep file on disk)", "Listeden kaldır (dosya kalır)"),
                Child = delIcon
            };
            delBtn.MouseEnter += (_, _) =>
            {
                delBtn.Background = BgRaisedHover;
                delIcon.Foreground = TextPrimary;
            };
            delBtn.MouseLeave += (_, _) =>
            {
                delBtn.Background = Brushes.Transparent;
                delIcon.Foreground = TextFaint;
            };
            delBtn.MouseLeftButtonUp += (_, ev) =>
            {
                ev.Handled = true;
                AppConfig.RemoveHistoryItem(item, deleteFileFromDisk: false);
                RefreshHistoryUI();
            };
            delBtn.MouseRightButtonUp += (_, ev) =>
            {
                ev.Handled = true;
                AppConfig.RemoveHistoryItem(item, deleteFileFromDisk: true);
                RefreshHistoryUI();
            };

            actionsRow.Children.Add(trashBtn);
            actionsRow.Children.Add(delBtn);
            Grid.SetColumn(actionsRow, 5);

            g.Children.Add(fileNameTb);
            g.Children.Add(formatBadge);
            g.Children.Add(sizeTb);
            g.Children.Add(timeTb);
            g.Children.Add(actionTb);
            g.Children.Add(actionsRow);
            rowContainer.Child = g;

            rowContainer.MouseEnter += (_, _) =>
            {
                rowContainer.Background = BgNavActive;
                actionTb.Foreground = TextPrimary;
            };
            rowContainer.MouseLeave += (_, _) =>
            {
                rowContainer.Background = Brushes.Transparent;
                actionTb.Foreground = TextMuted;
            };
            rowContainer.MouseLeftButtonUp += (_, _) =>
            {
                try
                {
                    if (File.Exists(item.OutputPath))
                    {
                        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.OutputPath}\"") { UseShellExecute = true });
                    }
                    else if (Directory.Exists(item.OutputPath))
                    {
                        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{item.OutputPath}\"") { UseShellExecute = true });
                    }
                }
                catch { }
            };

            _historyRowsPanel.Children.Add(rowContainer);
            if (i < AppConfig.Data.History.Count - 1)
            {
                _historyRowsPanel.Children.Add(new Border { Height = 1, Background = RuleHairline });
            }
        }
    }

    private static (Border RowBorder, TextBlock Label) CreateNavRow(string iconGlyph, Action onClick)
    {
        var border = new Border
        {
            Height = 36,
            CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(0, 0, 0, 4),
            Cursor = Cursors.Hand
        };
        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = iconGlyph,
            FontFamily = IconFont,
            FontSize = 13.5,
            Foreground = TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });
        var label = new TextBlock
        {
            FontSize = 13.5,
            Foreground = TextMuted,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(label);
        border.Child = stack;
        border.MouseLeftButtonUp += (_, _) => onClick();
        return (border, label);
    }

    private static Border CreateTopIconPill(out TextBlock label, Action onClick, double width, bool isIconFont)
    {
        label = new TextBlock
        {
            FontFamily = isIconFont ? IconFont : new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = isIconFont ? 11 : 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextMuted,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var pill = new Border
        {
            Width = width,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = BgSurface,
            BorderBrush = BorderOuter,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = Cursors.Hand,
            Child = label
        };
        var capturedLabel = label;
        pill.MouseEnter += (_, _) =>
        {
            pill.Background = BgRaised;
            capturedLabel.Foreground = TextPrimary;
        };
        pill.MouseLeave += (_, _) =>
        {
            pill.Background = BgSurface;
            capturedLabel.Foreground = TextMuted;
        };
        pill.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return pill;
    }

    private static StackPanel CreateKpiCell(out TextBlock labelTb, out TextBlock valueTb, out TextBlock subTb)
    {
        var sp = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        labelTb = new TextBlock
        {
            FontSize = 12,
            Foreground = TextMuted,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        valueTb = new TextBlock
        {
            FontSize = 23,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimary,
            Margin = new Thickness(0, 4, 0, 3)
        };
        Typography.SetNumeralAlignment(valueTb, FontNumeralAlignment.Tabular);
        subTb = new TextBlock
        {
            FontSize = 11.5,
            Foreground = TextFaint,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        sp.Children.Add(labelTb);
        sp.Children.Add(valueTb);
        sp.Children.Add(subTb);
        return sp;
    }

    private static Grid CreateTableGridRow()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Файл
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });  // Формат
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) }); // Размер
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });  // Время
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });  // Папка
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });  // Корзина + Убрать
        return g;
    }

    private static TextBlock CreateColumnHeaderLabel(TextAlignment align = TextAlignment.Left)
    {
        return new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = TextFaint,
            TextAlignment = align
        };
    }

    private static Grid CreateSettingRow(
        out TextBlock titleTb,
        out TextBlock descTb,
        out Border switchTrack,
        out Border switchKnob,
        Action onToggle)
    {
        var row = new Grid
        {
            Margin = new Thickness(0, 9, 0, 9),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        titleTb = new TextBlock
        {
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimary
        };
        descTb = new TextBlock
        {
            FontSize = 12,
            Foreground = TextFaint,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        textStack.Children.Add(titleTb);
        textStack.Children.Add(descTb);
        Grid.SetColumn(textStack, 0);

        switchKnob = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = BgGround,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        switchTrack = new Border
        {
            Width = 42,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(3, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = switchKnob
        };
        Grid.SetColumn(switchTrack, 1);

        row.Children.Add(textStack);
        row.Children.Add(switchTrack);
        row.MouseLeftButtonUp += (_, _) => onToggle();
        return row;
    }

    private static void UpdateSwitchVisual(Border track, Border knob, bool isOn)
    {
        track.Background = isOn ? AccentLime : BgRaisedHover;
        knob.Background = isOn ? BgGround : TextMuted;
        knob.HorizontalAlignment = isOn ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }
}

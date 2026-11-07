using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ShiftConvertApp;

/// <summary>
/// Глобальный хук перетаскивания (0% CPU в простое).
/// Срабатывает только когда зажата ЛКМ, мышь сдвинулась > 18px и нажат Shift.
/// </summary>
public static class GlobalDragHook
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;

    private const int VK_SHIFT = 0x10;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static IntPtr _mouseHookId = IntPtr.Zero;
    private static IntPtr _kbdHookId = IntPtr.Zero;
    private static LowLevelProc? _mouseProc;
    private static LowLevelProc? _kbdProc;

    private static bool _lButtonDown;
    private static bool _shiftHeldAtStart;
    private static bool _dragThresholdMet;
    private static bool _wheelTriggeredThisDrag;
    private static int _dragStartX;
    private static int _dragStartY;
    private static int _lastMouseX;
    private static int _lastMouseY;

    public static void Initialize()
    {
        if (_mouseHookId != IntPtr.Zero) return;
        _mouseProc = MouseHookCallback;
        _kbdProc = KeyboardHookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        IntPtr hMod = GetModuleHandle(curModule?.ModuleName);
        _mouseHookId = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
        _kbdHookId = SetWindowsHookEx(WH_KEYBOARD_LL, _kbdProc, hMod, 0);
    }

    public static void Shutdown()
    {
        if (_mouseHookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHookId);
            _mouseHookId = IntPtr.Zero;
        }
        if (_kbdHookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_kbdHookId);
            _kbdHookId = IntPtr.Zero;
        }
    }

    private static IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && AppConfig.Data.HoldShiftEnabled)
        {
            int msg = (int)wParam;
            if (msg == WM_MOUSEMOVE)
            {
                if (!_lButtonDown) return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
                try
                {
                    var hs = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    _lastMouseX = hs.pt.x;
                    _lastMouseY = hs.pt.y;

                    if (!_dragThresholdMet)
                    {
                        int dx = _lastMouseX - _dragStartX;
                        int dy = _lastMouseY - _dragStartY;
                        if (dx * dx + dy * dy >= 144) _dragThresholdMet = true; // >= 12px
                    }

                    if (_dragThresholdMet && !_wheelTriggeredThisDrag)
                    {
                        if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0)
                        {
                            _wheelTriggeredThisDrag = true;
                            RadialWheelWindow.TryShowAtCursor(_lastMouseX, _lastMouseY);
                        }
                    }
                }
                catch { }
            }
            else if (msg == WM_LBUTTONDOWN)
            {
                try
                {
                    var hs = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    _lButtonDown = true;
                    _dragStartX = hs.pt.x;
                    _dragStartY = hs.pt.y;
                    _lastMouseX = hs.pt.x;
                    _lastMouseY = hs.pt.y;
                    _dragThresholdMet = false;
                    _wheelTriggeredThisDrag = false;
                    _shiftHeldAtStart = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
                }
                catch { }
            }
            else if (msg == WM_LBUTTONUP)
            {
                _lButtonDown = false;
                _dragThresholdMet = false;
                _wheelTriggeredThisDrag = false;
            }
        }
        return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
    }

    private static IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _lButtonDown && _dragThresholdMet && !_wheelTriggeredThisDrag && AppConfig.Data.HoldShiftEnabled)
        {
            int msg = (int)wParam;
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                try
                {
                    var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    if (kb.vkCode is VK_SHIFT or VK_LSHIFT or VK_RSHIFT)
                    {
                        if ((GetAsyncKeyState(0x01) & 0x8000) != 0)
                        {
                            _wheelTriggeredThisDrag = true;
                            RadialWheelWindow.TryShowAtCursor(_lastMouseX, _lastMouseY);
                        }
                    }
                }
                catch { }
            }
        }
        return CallNextHookEx(_kbdHookId, nCode, wParam, lParam);
    }
}

/// <summary>
/// Радиальное колесо конвертации файлов в стиле Apple Liquid Glass.
/// </summary>
public sealed class RadialWheelWindow : Window
{
    private const double WheelSize = 320.0;
    private const double Center = WheelSize / 2.0;
    private const double InnerRadius = 46.0;
    private const double OuterRadius = 144.0;
    private const double IconRadius = 96.0;

    private static RadialWheelWindow? _active;
    private static long _lastCloseTicks;

    private readonly Grid _rootGrid;
    private readonly Canvas _sectorCanvas;
    private readonly ScaleTransform _scaleTransform;
    private readonly TextBlock _centerIconText;
    private readonly TextBlock _centerTitleText;
    private readonly TextBlock _centerSubText;
    private readonly Border _centerHub;

    private readonly DispatcherTimer _probeTimer;
    private readonly DispatcherTimer _safetyTimer;

    private List<ConvertOption> _options = new();
    private readonly List<System.Windows.Shapes.Path> _sectorPaths = new();
    private readonly List<TextBlock> _sectorIcons = new();
    private readonly List<TextBlock> _sectorLabels = new();
    private static readonly FontFamily IconFont = new("Segoe MDL2 Assets");
    private static readonly Color LimeColor = Color.FromRgb(0xC6, 0xF2, 0x4E);
    private static readonly Color LimeSoftBg = Color.FromRgb(0x22, 0x2A, 0x10);
    private string[] _draggedFiles = Array.Empty<string>();
    private int _hoveredIndex = -1;
    private bool _confirmedFileDrag;
    private bool _isClickMode;
    private bool _isOneShot;
    private bool _isClosing;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;
    private const int VK_ESCAPE = 0x1B;

    public static void TryShowAtCursor(int screenPxX, int screenPxY, bool isOneShot = false)
    {
        if (_active != null) return;
        if (Environment.TickCount64 - _lastCloseTicks < 350) return;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (_active != null) return;
            try
            {
                var files = ConvertEngine.GetSelectedExplorerFiles();
                if (files != null && files.Length > 0)
                {
                    ShowForFilesAtCursor(files, screenPxX, screenPxY, isOneShot);
                    return;
                }

                var win = new RadialWheelWindow(screenPxX, screenPxY, null, isOneShot);
                _active = win;
                win.Show();
                mouse_event(MOUSEEVENTF_MOVE, 1, 1, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_MOVE, -1, -1, 0, UIntPtr.Zero);
            }
            catch
            {
                _active = null;
                if (isOneShot) Application.Current?.Shutdown();
            }
        });
    }

    public static void ShowForFilesAtCursor(string[] files, int screenPxX, int screenPxY, bool isOneShot)
    {
        if (files == null || files.Length == 0)
        {
            if (isOneShot) Application.Current?.Shutdown();
            return;
        }

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try { _active?.CloseWheel(suppressShutdown: true); } catch { }
            try
            {
                var win = new RadialWheelWindow(screenPxX, screenPxY, files, isOneShot);
                _active = win;
                win.Show();
                win.Activate();
            }
            catch
            {
                _active = null;
                if (isOneShot) Application.Current?.Shutdown();
            }
        });
    }

    private RadialWheelWindow(int screenPxX, int screenPxY, string[]? preloadedFiles, bool isOneShot)
    {
        _isOneShot = isOneShot;
        _isClickMode = preloadedFiles != null && preloadedFiles.Length > 0;

        Width = WheelSize;
        Height = WheelSize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = _isClickMode;
        ResizeMode = ResizeMode.NoResize;
        AllowDrop = true;
        Opacity = _isClickMode ? 1.0 : 0.01;
        if (_isClickMode) Cursor = Cursors.Hand;

        PositionAtScreenPixels(screenPxX, screenPxY);

        _scaleTransform = new ScaleTransform(0.72, 0.72, Center, Center);
        _rootGrid = new Grid
        {
            Width = WheelSize,
            Height = WheelSize,
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            RenderTransform = _scaleTransform
        };

        var outerDisc = new Ellipse
        {
            Width = OuterRadius * 2 + 12,
            Height = OuterRadius * 2 + 12,
            Fill = new SolidColorBrush(Color.FromArgb(0xF6, 0x05, 0x05, 0x05)),
            Stroke = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
            StrokeThickness = 1.0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _rootGrid.Children.Add(outerDisc);

        _sectorCanvas = new Canvas
        {
            Width = WheelSize,
            Height = WheelSize,
            IsHitTestVisible = false
        };
        _rootGrid.Children.Add(_sectorCanvas);

        _centerHub = new Border
        {
            Width = InnerRadius * 2 - 4,
            Height = InnerRadius * 2 - 4,
            CornerRadius = new CornerRadius(InnerRadius),
            Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0D)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            BorderThickness = new Thickness(1.0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        var centerStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _centerIconText = new TextBlock
        {
            Text = "\uE945",
            FontFamily = IconFont,
            FontSize = 15,
            Foreground = new SolidColorBrush(LimeColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 3)
        };

        _centerTitleText = new TextBlock
        {
            Text = "Shift",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _centerSubText = new TextBlock
        {
            Text = Loc.T("Отмена", "Cancel", "İptal"),
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0x94, 0x94)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = InnerRadius * 1.75
        };

        centerStack.Children.Add(_centerIconText);
        centerStack.Children.Add(_centerTitleText);
        centerStack.Children.Add(_centerSubText);
        _centerHub.Child = centerStack;
        _rootGrid.Children.Add(_centerHub);

        Content = _rootGrid;

        _probeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _probeTimer.Tick += (_, _) =>
        {
            _probeTimer.Stop();
            if (!_confirmedFileDrag && !_isClickMode) CloseWheel();
        };

        _safetyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _safetyTimer.Tick += (_, _) =>
        {
            if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0)
            {
                CloseWheel();
                return;
            }
            if (!_isClickMode && _confirmedFileDrag && (GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
            {
                _safetyTimer.Stop();
                Dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(110);
                    if (!_isClosing) CloseWheel();
                });
            }
        };

        if (_isClickMode && preloadedFiles != null)
        {
            _confirmedFileDrag = true;
            _draggedFiles = preloadedFiles;
            _options = ConvertEngine.GetOptionsForFiles(preloadedFiles, forWheel: true);
            BuildSectors();
            SetHoveredSector(-1);
            RevealWheelAnimation();
            _safetyTimer.Start();

            Deactivated += (_, _) =>
            {
                if (!_isClosing) CloseWheel();
            };
        }
        else
        {
            _probeTimer.Start();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isClickMode)
        {
            UpdateHoverFromPoint(e.GetPosition(_rootGrid));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_isClickMode) return;
        e.Handled = true;

        int chosenIndex = _hoveredIndex;
        var files = _draggedFiles;
        var options = _options;
        bool oneShot = _isOneShot;

        if (chosenIndex >= 0 && chosenIndex < options.Count && files.Length > 0)
        {
            CloseWheel(suppressShutdown: true);
            ExecuteAndNotify(files, options[chosenIndex], shutdownOnComplete: oneShot);
        }
        else
        {
            CloseWheel();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            CloseWheel();
        }
    }

    private void PositionAtScreenPixels(int pxX, int pxY)
    {
        double dpiX = 1.0, dpiY = 1.0;
        try
        {
            IntPtr hdc = GetDC(IntPtr.Zero);
            if (hdc != IntPtr.Zero)
            {
                int logX = GetDeviceCaps(hdc, 88);
                int logY = GetDeviceCaps(hdc, 90);
                ReleaseDC(IntPtr.Zero, hdc);
                if (logX > 0) dpiX = logX / 96.0;
                if (logY > 0) dpiY = logY / 96.0;
            }
        }
        catch { }

        double dipX = (pxX / dpiX) - Center;
        double dipY = (pxY / dpiY) - Center;

        double maxW = SystemParameters.VirtualScreenWidth;
        double maxH = SystemParameters.VirtualScreenHeight;
        double minX = SystemParameters.VirtualScreenLeft;
        double minY = SystemParameters.VirtualScreenTop;

        Left = Math.Clamp(dipX, minX + 8, minX + maxW - WheelSize - 8);
        Top = Math.Clamp(dipY, minY + 8, minY + maxH - WheelSize - 8);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            int addStyle = _isClickMode
                ? NativeMethods.WS_EX_TOOLWINDOW
                : (NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle | addStyle);
        }
        catch { }
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Length > 0)
        {
            _probeTimer.Stop();
            if (!_confirmedFileDrag)
            {
                _confirmedFileDrag = true;
                _draggedFiles = files;
                _options = ConvertEngine.GetOptionsForFiles(files, forWheel: true);
                BuildSectors();
                RevealWheelAnimation();
                _safetyTimer.Start();
            }
            e.Effects = DragDropEffects.Copy;
            UpdateHoverFromPoint(e.GetPosition(_rootGrid));
        }
        else
        {
            e.Effects = DragDropEffects.None;
            CloseWheel();
        }
        e.Handled = true;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        if (_confirmedFileDrag)
        {
            e.Effects = DragDropEffects.Copy;
            UpdateHoverFromPoint(e.GetPosition(_rootGrid));
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        SetHoveredSector(-1);
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        e.Handled = true;

        int chosenIndex = _hoveredIndex;
        var files = _draggedFiles;
        var options = _options;

        CloseWheel();

        if (chosenIndex >= 0 && chosenIndex < options.Count && files.Length > 0)
        {
            ExecuteAndNotify(files, options[chosenIndex]);
        }
    }

    private static CancellationTokenSource? _activeCts;

    public static void CancelActiveConversion()
    {
        try
        {
            _activeCts?.Cancel();
        }
        catch { }
    }

    public static void ExecuteAndNotify(IReadOnlyList<string> files, ConvertOption opt, bool shutdownOnComplete = false)
    {
        try { _activeCts?.Cancel(); } catch { }
        try { _activeCts?.Dispose(); } catch { }
        var cts = new CancellationTokenSource();
        _activeCts = cts;

        bool showToast = AppConfig.Data.ShowToast || shutdownOnComplete || App.IsOneShotMode;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            App.Hub?.SetConvertingState(true, files, opt);
            if (showToast)
            {
                FloatingToastWindow.ShowLoading(files, opt);
            }
        });

        Task.Run(async () =>
        {
            long startTicks = Environment.TickCount64;
            var res = ConvertEngine.ConvertFiles(files, opt.Key, pct =>
            {
                if (cts.IsCancellationRequested) return;
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    App.Hub?.UpdateProgress(pct, files, opt);
                    if (showToast)
                    {
                        FloatingToastWindow.UpdateProgress(pct, opt);
                    }
                });
            }, cts.Token);

            long elapsed = Environment.TickCount64 - startTicks;
            if (!res.Cancelled && elapsed < 420)
            {
                await Task.Delay((int)(420 - elapsed)).ConfigureAwait(false);
            }

            if (ReferenceEquals(_activeCts, cts))
            {
                _activeCts = null;
            }
            try { cts.Dispose(); } catch { }

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                App.Hub?.SetConvertingState(false, files, opt);
                App.Hub?.RefreshHistoryUI();
                if (showToast)
                {
                    FloatingToastWindow.CompleteToast(res, opt);
                }
                else if (shutdownOnComplete || App.IsOneShotMode)
                {
                    Application.Current?.Shutdown();
                }
                MemoryOptimizer.ScheduleTrim(2200);
            });
        });
    }

    private void RevealWheelAnimation()
    {
        Opacity = 1.0;
        var dur = TimeSpan.FromMilliseconds(165);
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
        _scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.68, 1.0, dur) { EasingFunction = ease });
        _scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.68, 1.0, dur) { EasingFunction = ease });
    }

    private void BuildSectors()
    {
        _sectorCanvas.Children.Clear();
        _sectorPaths.Clear();
        _sectorIcons.Clear();
        _sectorLabels.Clear();

        int count = _options.Count;
        if (count == 0) return;

        double stepAngle = 360.0 / count;
        double gapDeg = count >= 7 ? 2.2 : 2.8;

        for (int i = 0; i < count; i++)
        {
            var opt = _options[i];
            double startDeg = -90.0 + i * stepAngle - stepAngle / 2.0 + gapDeg / 2.0;
            double endDeg = startDeg + stepAngle - gapDeg;

            var path = new System.Windows.Shapes.Path
            {
                Data = CreateSectorGeometry(Center, Center, InnerRadius + 3, OuterRadius, startDeg, endDeg),
                Fill = new SolidColorBrush(Color.FromRgb(0x13, 0x13, 0x13)),
                Stroke = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24)),
                StrokeThickness = 1.0
            };
            _sectorPaths.Add(path);
            _sectorCanvas.Children.Add(path);

            double midRad = (-90.0 + i * stepAngle) * Math.PI / 180.0;
            double bx = Center + IconRadius * Math.Cos(midRad);
            double by = Center + IconRadius * Math.Sin(midRad);

            var badgeStack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var iconTb = new TextBlock
            {
                Text = opt.Icon,
                FontFamily = IconFont,
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0xB4, 0xB4)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 3)
            };
            _sectorIcons.Add(iconTb);
            badgeStack.Children.Add(iconTb);

            var labelTb = new TextBlock
            {
                Text = opt.Label,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _sectorLabels.Add(labelTb);
            badgeStack.Children.Add(labelTb);

            var badge = new Border
            {
                Width = 64,
                Height = 44,
                CornerRadius = new CornerRadius(10),
                Child = badgeStack
            };

            Canvas.SetLeft(badge, bx - 32);
            Canvas.SetTop(badge, by - 22);
            _sectorCanvas.Children.Add(badge);
        }
    }

    private void UpdateHoverFromPoint(Point pt)
    {
        if (_options.Count == 0) return;

        double dx = pt.X - Center;
        double dy = pt.Y - Center;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        if (dist < InnerRadius + 2 || dist > OuterRadius + 24)
        {
            SetHoveredSector(-1);
            return;
        }

        double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        double step = 360.0 / _options.Count;
        double shifted = (angle + 90.0 + step / 2.0 + 360.0) % 360.0;
        int idx = (int)(shifted / step) % _options.Count;
        SetHoveredSector(idx);
    }

    private void SetHoveredSector(int idx)
    {
        if (_hoveredIndex == idx) return;
        _hoveredIndex = idx;

        for (int i = 0; i < _sectorPaths.Count; i++)
        {
            bool active = (i == idx);
            _sectorPaths[i].Fill = active
                ? new SolidColorBrush(LimeSoftBg)
                : new SolidColorBrush(Color.FromRgb(0x13, 0x13, 0x13));
            _sectorPaths[i].Stroke = active
                ? new SolidColorBrush(LimeColor)
                : new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24));
            _sectorPaths[i].StrokeThickness = active ? 1.6 : 1.0;

            if (i < _sectorIcons.Count)
            {
                _sectorIcons[i].Foreground = active
                    ? new SolidColorBrush(LimeColor)
                    : new SolidColorBrush(Color.FromRgb(0xB4, 0xB4, 0xB4));
            }
            if (i < _sectorLabels.Count)
            {
                _sectorLabels[i].Foreground = active
                    ? new SolidColorBrush(LimeColor)
                    : Brushes.White;
            }
        }

        if (idx >= 0 && idx < _options.Count)
        {
            var opt = _options[idx];
            _centerIconText.Text = opt.Icon;
            _centerIconText.Foreground = new SolidColorBrush(LimeColor);
            _centerTitleText.Text = opt.Label;
            _centerTitleText.Foreground = new SolidColorBrush(LimeColor);
            _centerSubText.Text = opt.SubLabel;
            _centerHub.BorderBrush = new SolidColorBrush(LimeColor);
        }
        else
        {
            _centerIconText.Text = "\uE711";
            _centerIconText.Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0x94, 0x94));
            _centerTitleText.Text = Loc.T("Отмена", "Cancel", "İptal");
            _centerTitleText.Foreground = Brushes.White;
            _centerSubText.Text = _draggedFiles.Length > 1
                ? $"{_draggedFiles.Length} {Loc.T("файл.", "files", "dosya")}"
                : System.IO.Path.GetExtension(_draggedFiles.FirstOrDefault() ?? "").ToUpperInvariant().TrimStart('.');
            _centerHub.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        }
    }

    private static Geometry CreateSectorGeometry(double cx, double cy, double rInner, double rOuter, double startDeg, double endDeg)
    {
        double startRad = startDeg * Math.PI / 180.0;
        double endRad = endDeg * Math.PI / 180.0;

        Point p1 = new(cx + rOuter * Math.Cos(startRad), cy + rOuter * Math.Sin(startRad));
        Point p2 = new(cx + rOuter * Math.Cos(endRad), cy + rOuter * Math.Sin(endRad));
        Point p3 = new(cx + rInner * Math.Cos(endRad), cy + rInner * Math.Sin(endRad));
        Point p4 = new(cx + rInner * Math.Cos(startRad), cy + rInner * Math.Sin(startRad));

        bool largeArc = (endDeg - startDeg) > 180.0;

        var fig = new PathFigure { StartPoint = p1, IsClosed = true, IsFilled = true };
        fig.Segments.Add(new ArcSegment
        {
            Point = p2,
            Size = new Size(rOuter, rOuter),
            IsLargeArc = largeArc,
            SweepDirection = SweepDirection.Clockwise
        });
        fig.Segments.Add(new LineSegment(p3, isStroked: true));
        fig.Segments.Add(new ArcSegment
        {
            Point = p4,
            Size = new Size(rInner, rInner),
            IsLargeArc = largeArc,
            SweepDirection = SweepDirection.Counterclockwise
        });

        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        return geo;
    }

    private void CloseWheel(bool suppressShutdown = false)
    {
        if (_isClosing) return;
        _isClosing = true;
        _probeTimer.Stop();
        _safetyTimer.Stop();
        _lastCloseTicks = Environment.TickCount64;
        if (_active == this) _active = null;
        try { Close(); } catch { }
        if ((_isOneShot || App.IsOneShotMode) && !suppressShutdown)
        {
            Application.Current?.Shutdown();
            return;
        }
        MemoryOptimizer.ScheduleTrim(1500);
    }
}

/// <summary>
/// Всплывающая капсула сверху экрана: сначала показывает процесс загрузки (вращающийся векторный индикатор + полоса прогресса),
/// а затем плавно переключается в статус завершения и закрывается через 3.2 с.
/// </summary>
public sealed class FloatingToastWindow : Window
{
    private static FloatingToastWindow? _current;
    private readonly DispatcherTimer _closeTimer;
    private readonly Border _iconBorder;
    private readonly Grid _iconHost;
    private readonly System.Windows.Shapes.Path _spinnerArc;
    private readonly RotateTransform _spinnerRotate;
    private readonly TextBlock _doneIconTb;
    private readonly TextBlock _titleTb;
    private readonly TextBlock _msgTb;
    private readonly Border _cancelBtn;
    private readonly TextBlock _cancelLabelTb;
    private readonly Border _progressTrack;
    private readonly Border _progressFill;
    private string? _outputPath;

    public static void ShowLoading(IReadOnlyList<string> files, ConvertOption opt)
    {
        try { _current?.Close(); } catch { }
        var w = new FloatingToastWindow();
        _current = w;
        w.Show();
        w.SetLoadingContent(files, opt);
    }

    public static void UpdateProgress(double pct, ConvertOption opt)
    {
        if (_current == null || !_current.IsVisible) return;
        _current.SetProgressValue(pct, opt);
    }

    public static void CompleteToast(ConvertResult res, ConvertOption opt)
    {
        if (_current == null || !_current.IsVisible)
        {
            var w = new FloatingToastWindow();
            _current = w;
            w.Show();
        }
        _current.SetCompletedContent(res);
    }

    private FloatingToastWindow()
    {
        Width = 420;
        Height = 84;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;

        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2.0;
        Top = 14;

        var pill = new Border
        {
            Margin = new Thickness(12, 8, 12, 12),
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            BorderThickness = new Thickness(1.0),
            Padding = new Thickness(16, 10, 14, 8),
            Cursor = Cursors.Hand
        };

        var outerStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _iconHost = new Grid { Width = 30, Height = 30 };

        _spinnerRotate = new RotateTransform(0, 9, 9);
        _spinnerArc = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 9,1 A 8,8 0 1 1 1,9"),
            Stroke = new SolidColorBrush(Color.FromRgb(0xC6, 0xF2, 0x4E)),
            StrokeThickness = 2.0,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _spinnerRotate
        };

        _doneIconTb = new TextBlock
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };

        _iconHost.Children.Add(_spinnerArc);
        _iconHost.Children.Add(_doneIconTb);

        _iconBorder = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x10)),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _iconHost
        };
        Grid.SetColumn(_iconBorder, 0);

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _titleTb = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _msgTb = new TextBlock
        {
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0xB4, 0xB4)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 0)
        };
        textStack.Children.Add(_titleTb);
        textStack.Children.Add(_msgTb);
        Grid.SetColumn(textStack, 1);

        var cancelRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var cancelIcon = new TextBlock
        {
            Text = "\uE711",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 9.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x56, 0x6F)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        };
        _cancelLabelTb = new TextBlock
        {
            Text = Loc.T("Отменить", "Cancel", "İptal"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x56, 0x6F)),
            VerticalAlignment = VerticalAlignment.Center
        };
        cancelRow.Children.Add(cancelIcon);
        cancelRow.Children.Add(_cancelLabelTb);

        _cancelBtn = new Border
        {
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(Color.FromRgb(0x23, 0x15, 0x19)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x24, 0x2D)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = Loc.T("Остановить конвертацию и удалить неполный файл", "Stop conversion and remove partial file", "Dönüştürmeyi durdur ve eksik dosyayı sil"),
            Child = cancelRow
        };
        _cancelBtn.MouseEnter += (_, _) =>
        {
            _cancelBtn.Background = new SolidColorBrush(Color.FromRgb(0x36, 0x1A, 0x22));
            _cancelBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x56, 0x6F));
        };
        _cancelBtn.MouseLeave += (_, _) =>
        {
            _cancelBtn.Background = new SolidColorBrush(Color.FromRgb(0x23, 0x15, 0x19));
            _cancelBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x24, 0x2D));
        };
        _cancelBtn.MouseLeftButtonDown += (_, ev) =>
        {
            ev.Handled = true;
            RadialWheelWindow.CancelActiveConversion();
        };
        _cancelBtn.MouseLeftButtonUp += (_, ev) => ev.Handled = true;
        Grid.SetColumn(_cancelBtn, 2);

        grid.Children.Add(_iconBorder);
        grid.Children.Add(textStack);
        grid.Children.Add(_cancelBtn);
        outerStack.Children.Add(grid);

        _progressFill = new Border
        {
            Height = 3,
            Width = 24,
            CornerRadius = new CornerRadius(1.5),
            Background = new SolidColorBrush(Color.FromRgb(0xC6, 0xF2, 0x4E)),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _progressTrack = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            Margin = new Thickness(0, 7, 0, 0),
            Child = _progressFill
        };
        outerStack.Children.Add(_progressTrack);

        pill.Child = outerStack;

        pill.MouseLeftButtonUp += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_outputPath))
            {
                try
                {
                    if (File.Exists(_outputPath))
                    {
                        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_outputPath}\"") { UseShellExecute = true });
                    }
                    else if (Directory.Exists(_outputPath))
                    {
                        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_outputPath}\"") { UseShellExecute = true });
                    }
                }
                catch { }
                CloseToast();
            }
        };

        Content = pill;

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3200) };
        _closeTimer.Tick += (_, _) => CloseToast();
    }

    private void SetLoadingContent(IReadOnlyList<string> files, ConvertOption opt)
    {
        _closeTimer.Stop();
        _outputPath = null;

        _iconBorder.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x10));
        _spinnerArc.Visibility = Visibility.Visible;
        _doneIconTb.Visibility = Visibility.Collapsed;
        _cancelLabelTb.Text = Loc.T("Отменить", "Cancel", "İptal");
        _cancelBtn.Visibility = Visibility.Visible;

        var spinAnim = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(650))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        _spinnerRotate.BeginAnimation(RotateTransform.AngleProperty, spinAnim);

        string targetName = files.Count == 1
            ? System.IO.Path.GetFileName(files[0])
            : $"{files.Count} {Loc.T("файл(ов)", "files", "dosya")}";

        _titleTb.Text = Loc.T(
            $"Выполняется: {opt.Label}…",
            $"Processing: {opt.Label}…",
            $"İşleniyor: {opt.Label}…");
        _msgTb.Text = $"{targetName} · {Loc.T("Подождите…", "Please wait…", "Lütfen bekleyin…")}";

        _progressTrack.Visibility = Visibility.Visible;
        _progressFill.Background = new SolidColorBrush(Color.FromRgb(0xC6, 0xF2, 0x4E));
        var barAnim = new DoubleAnimation(18, 290, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        _progressFill.BeginAnimation(WidthProperty, barAnim);
    }

    private void SetProgressValue(double pct, ConvertOption opt)
    {
        int clamped = Math.Clamp((int)Math.Round(pct), 1, 99);
        _titleTb.Text = Loc.T(
            $"Выполняется: {opt.Label} ({clamped}%)…",
            $"Processing: {opt.Label} ({clamped}%)…",
            $"İşleniyor: {opt.Label} (%{clamped})…");
        _progressFill.BeginAnimation(WidthProperty, null);
        _progressFill.Width = Math.Clamp(360.0 * (clamped / 100.0), 18.0, 356.0);
    }

    private void SetCompletedContent(ConvertResult res)
    {
        _outputPath = res.OutputPath;
        _spinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        _spinnerArc.Visibility = Visibility.Collapsed;
        _doneIconTb.Visibility = Visibility.Visible;
        _cancelBtn.Visibility = Visibility.Collapsed;

        Color accent = res.Success ? Color.FromRgb(0xC6, 0xF2, 0x4E) : Color.FromRgb(0xE5, 0x56, 0x6F);
        Color softBg = res.Success ? Color.FromRgb(0x22, 0x2A, 0x10) : Color.FromRgb(0x2C, 0x1A, 0x20);

        _iconBorder.Background = new SolidColorBrush(softBg);
        _doneIconTb.Text = res.Success ? "\uE73E" : "\uE711";
        _doneIconTb.Foreground = new SolidColorBrush(accent);

        _titleTb.Text = res.Title;
        _msgTb.Text = res.Message;

        _progressFill.Background = new SolidColorBrush(accent);
        var finishAnim = new DoubleAnimation(_progressFill.ActualWidth > 0 ? _progressFill.ActualWidth : 240, 360, TimeSpan.FromMilliseconds(140));
        _progressFill.BeginAnimation(WidthProperty, finishAnim);

        _closeTimer.Stop();
        _closeTimer.Interval = TimeSpan.FromMilliseconds(res.Cancelled ? 1800 : 3200);
        _closeTimer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
        }
        catch { }
    }

    private void CloseToast()
    {
        _closeTimer.Stop();
        if (_current == this) _current = null;
        try { Close(); } catch { }
        if (App.IsOneShotMode)
        {
            Application.Current?.Shutdown();
            return;
        }
        MemoryOptimizer.ScheduleTrim(1200);
    }
}

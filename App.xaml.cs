using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ShiftConvertApp;

public partial class App : Application
{
    private static Mutex? _mutex;
    public static HubWindow? Hub { get; private set; }
    public static bool IsOneShotMode { get; private set; }

    private HwndSource? _trayHwndSource;
    private uint _wakeMsg;
    private bool _trayAdded;
    private NativeMethods.NOTIFYICONDATA _nid;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, _) => { };
        DispatcherUnhandledException += (_, ev) => { ev.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, ev) => { ev.SetObserved(); };

        AppConfig.Load();
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Проверяем, запущено ли приложение из контекстного меню ПКМ (--wheel "путь") или перетаскиванием файла на .exe
        var cliFiles = ParseCliFiles(e.Args, out string? directAction);
        if (cliFiles.Count > 0)
        {
            IsOneShotMode = true;
            base.OnStartup(e);

            if (!string.IsNullOrEmpty(directAction))
            {
                var opts = ConvertEngine.GetOptionsForFiles(cliFiles, forWheel: false);
                var chosen = opts.FirstOrDefault(o => string.Equals(o.Key, directAction, StringComparison.OrdinalIgnoreCase))
                             ?? new ConvertOption { Key = directAction, Label = directAction.ToUpperInvariant(), Icon = "\uE945" };
                RadialWheelWindow.ExecuteAndNotify(cliFiles, chosen, shutdownOnComplete: true);
            }
            else
            {
                NativeMethods.GetCursorPos(out var pt);
                RadialWheelWindow.ShowForFilesAtCursor(cliFiles.ToArray(), pt.x, pt.y, isOneShot: true);
            }
            return;
        }

        bool isNew = true;
        try
        {
            _mutex = new Mutex(true, @"Local\ShiftConvert_SingleInstance_v1", out isNew);
        }
        catch { isNew = true; }

        if (!isNew)
        {
            uint wakeMsg = NativeMethods.RegisterWindowMessage("ShiftConvert_WakeHub_Msg");
            NativeMethods.PostMessage(new IntPtr(0xFFFF), wakeMsg, IntPtr.Zero, IntPtr.Zero);
            Shutdown();
            return;
        }

        // Очистка старых файлов после обновления и синхронизация контекстного меню ПКМ
        Task.Run(() =>
        {
            try
            {
                string? exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                foreach (var f in Directory.GetFiles(exeDir, "*delete_on_exit*"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }

            if (AppConfig.Data.ContextMenuEnabled)
            {
                AppConfig.SetContextMenu(true);
            }
        });

        base.OnStartup(e);

        InitLightweightTrayHost();
        GlobalDragHook.Initialize();

        // Если запущено с флагом --tray (при старте системы), вообще не создаём главное окно HubWindow —
        // процесс сразу остаётся в глубоком спящем режиме (~2-4 МБ ОЗУ, 0% CPU)!
        bool silent = e.Args.Length > 0 && string.Equals(e.Args[0], "--tray", StringComparison.OrdinalIgnoreCase);
        if (!silent)
        {
            ShowOrCreateHub();
        }
        else
        {
            MemoryOptimizer.ScheduleTrim(400);
        }
    }

    private static List<string> ParseCliFiles(string[] args, out string? directAction)
    {
        directAction = null;
        var list = new List<string>();
        if (args == null || args.Length == 0) return list;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(a, "--wheel", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(a, "--action", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                directAction = args[++i];
                continue;
            }
            string cleaned = a.Trim('"');
            if (File.Exists(cleaned) || Directory.Exists(cleaned))
            {
                list.Add(Path.GetFullPath(cleaned));
            }
        }
        return list;
    }

    /// <summary>
    /// Открывает окно HubWindow (создавая его по требованию).
    /// </summary>
    public static void ShowOrCreateHub()
    {
        if (Current is not App app) return;
        app.Dispatcher.Invoke(() =>
        {
            if (Hub == null)
            {
                Hub = new HubWindow();
            }
            Hub.ShowHub();
        });
    }

    /// <summary>
    /// Полностью выгружает тяжёлое окно HubWindow из оперативной памяти при сворачивании в фон.
    /// Хук клавиши Shift и иконка в трее продолжают работать с минимальным потреблением ОЗУ (~2-4 МБ).
    /// </summary>
    public static void UnloadHubToSleepMode()
    {
        if (Current is not App app) return;
        app.Dispatcher.InvokeAsync(() =>
        {
            if (Hub != null)
            {
                var h = Hub;
                Hub = null;
                h.ForceCloseForSleep();
            }
            MemoryOptimizer.ScheduleTrim(250);
        }, DispatcherPriority.Background);
    }

    public static void QuitApplication()
    {
        if (Current is not App app) return;
        app.DisposeTray();
        GlobalDragHook.Shutdown();
        if (Hub != null)
        {
            var h = Hub;
            Hub = null;
            h.ForceCloseForSleep();
        }
        app.Shutdown();
    }

    private void InitLightweightTrayHost()
    {
        if (_trayHwndSource != null) return;
        _wakeMsg = NativeMethods.RegisterWindowMessage("ShiftConvert_WakeHub_Msg");

        var p = new HwndSourceParameters("ShiftConvert_LightTrayHost")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0
        };
        _trayHwndSource = new HwndSource(p);
        _trayHwndSource.AddHook(TrayWndProc);

        IntPtr hIcon = IntPtr.Zero;
        try
        {
            string exePath = Environment.ProcessPath ?? "";
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                ushort idx = 0;
                hIcon = NativeMethods.ExtractAssociatedIcon(IntPtr.Zero, exePath, ref idx);
            }
        }
        catch { }

        if (hIcon == IntPtr.Zero)
        {
            hIcon = NativeMethods.LoadIcon(IntPtr.Zero, new IntPtr(32512));
        }

        _nid = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _trayHwndSource.Handle,
            uID = 1001,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = NativeMethods.WM_TRAYICON,
            hIcon = hIcon,
            szTip = "ShiftConvert (Sleep Mode · 0% CPU)"
        };

        _trayAdded = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref _nid);
    }

    public void DisposeTray()
    {
        if (_trayAdded)
        {
            _trayAdded = false;
            try { NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref _nid); } catch { }
        }
        try { _trayHwndSource?.Dispose(); _trayHwndSource = null; } catch { }
    }

    private IntPtr TrayWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_wakeMsg != 0 && (uint)msg == _wakeMsg)
        {
            ShowOrCreateHub();
            handled = true;
        }
        else if (msg == NativeMethods.WM_TRAYICON)
        {
            int ev = lParam.ToInt32() & 0xFFFF;
            if (ev is NativeMethods.WM_LBUTTONUP or NativeMethods.WM_LBUTTONDBLCLK)
            {
                if (Hub != null && Hub.IsVisible)
                {
                    UnloadHubToSleepMode();
                }
                else
                {
                    ShowOrCreateHub();
                }
                handled = true;
            }
            else if (ev == NativeMethods.WM_RBUTTONUP)
            {
                ShowTrayContextMenu();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void ShowTrayContextMenu()
    {
        var menu = new ContextMenu();
        var openItem = new MenuItem { Header = Loc.T("Открыть ShiftConvert", "Open ShiftConvert", "ShiftConvert'i Aç") };
        openItem.Click += (_, _) => ShowOrCreateHub();

        var shiftItem = new MenuItem
        {
            Header = Loc.T("Колесо по клавише Shift", "Radial Wheel on Shift", "Shift Tekerleği"),
            IsCheckable = true,
            IsChecked = AppConfig.Data.HoldShiftEnabled
        };
        shiftItem.Click += (_, _) =>
        {
            AppConfig.Data.HoldShiftEnabled = !AppConfig.Data.HoldShiftEnabled;
            AppConfig.Save();
            Hub?.RefreshHistoryUI();
        };

        var ctxItem = new MenuItem
        {
            Header = Loc.T("Меню правой кнопки мыши (ПКМ)", "Explorer Right-Click Menu", "Sağ Tık Menüsü"),
            IsCheckable = true,
            IsChecked = AppConfig.IsContextMenuEnabled()
        };
        ctxItem.Click += (_, _) =>
        {
            bool next = !AppConfig.IsContextMenuEnabled();
            AppConfig.Data.ContextMenuEnabled = next;
            AppConfig.SetContextMenu(next);
            AppConfig.Save();
            Hub?.RefreshHistoryUI();
        };

        var autoItem = new MenuItem
        {
            Header = Loc.T("Запускать вместе с Windows", "Launch at Windows Startup", "Windows ile Başlat"),
            IsCheckable = true,
            IsChecked = AppConfig.IsAutoStartEnabled()
        };
        autoItem.Click += (_, _) =>
        {
            AppConfig.SetAutoStart(!AppConfig.IsAutoStartEnabled());
            Hub?.RefreshHistoryUI();
        };

        var exitItem = new MenuItem { Header = Loc.T("Выключить полностью (оставить только ПКМ)", "Quit Background Process (Keep Right-Click)", "Arka Planı Kapat (Sadece Sağ Tık)") };
        exitItem.Click += (_, _) => QuitApplication();

        menu.Items.Add(openItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(shiftItem);
        menu.Items.Add(ctxItem);
        menu.Items.Add(autoItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);
        menu.IsOpen = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        GlobalDragHook.Shutdown();
        DisposeTray();
        try
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch { }
        base.OnExit(e);
    }
}

public static class MemoryOptimizer
{
    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr minSize, IntPtr maxSize);

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private static long _lastTrimTicks;

    public static void TrimNow()
    {
        try
        {
            long now = Environment.TickCount64;
            if (now - _lastTrimTicks < 400) return;
            _lastTrimTicks = now;

            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);

            IntPtr handle = Process.GetCurrentProcess().Handle;
            EmptyWorkingSet(handle);
            SetProcessWorkingSetSize(handle, new IntPtr(-1), new IntPtr(-1));
        }
        catch { }
    }

    public static void ScheduleTrim(int delayMs = 800)
    {
        Task.Run(async () =>
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            TrimNow();
        });
    }
}

public sealed class HistoryEntry
{
    public string FileName { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string Format { get; set; } = "";
    public string SizeInfo { get; set; } = "";
    public string TimeText { get; set; } = "";
}

public sealed class AppConfigData
{
    public string Lang { get; set; } = "ru";
    public bool AutoCopyClipboard { get; set; } = true;
    public bool ShowToast { get; set; } = true;
    public bool HoldShiftEnabled { get; set; } = true;
    public bool ContextMenuEnabled { get; set; } = true;
    public int TotalConvertedCount { get; set; } = 0;
    public long TotalSavedBytes { get; set; } = 0;
    public List<HistoryEntry> History { get; set; } = new();
}

public static class AppConfig
{
    private const string RunRegPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ShiftConvert";
    private const string FileShellKeyPath = @"Software\Classes\*\shell\ShiftConvert";
    private const string DirShellKeyPath = @"Software\Classes\Directory\shell\ShiftConvert";

    public static AppConfigData Data { get; private set; } = new();

    private static string ConfigPath
    {
        get
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShiftConvert");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "config.json");
        }
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize<AppConfigData>(json);
                if (loaded != null)
                {
                    Data = loaded;
                    if (Data.History.Count > 5)
                    {
                        Data.History.RemoveRange(5, Data.History.Count - 5);
                        Save();
                    }
                }
            }
        }
        catch { }
    }

    public static void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch { }
    }

    public static void AddHistory(string outputPath, string format, string sizeInfo, long savedBytes = 0, int convertedCount = 1)
    {
        if (string.IsNullOrEmpty(outputPath)) return;
        Data.TotalConvertedCount += Math.Max(1, convertedCount);
        if (savedBytes > 0) Data.TotalSavedBytes += savedBytes;

        Data.History.RemoveAll(h => string.Equals(h.OutputPath, outputPath, StringComparison.OrdinalIgnoreCase));
        Data.History.Insert(0, new HistoryEntry
        {
            FileName = Path.GetFileName(outputPath),
            OutputPath = outputPath,
            Format = format.ToUpperInvariant(),
            SizeInfo = sizeInfo,
            TimeText = DateTime.Now.ToString("HH:mm")
        });
        if (Data.History.Count > 5)
        {
            Data.History.RemoveRange(5, Data.History.Count - 5);
        }
        Save();
    }

    public static void RemoveHistoryItem(HistoryEntry item, bool deleteFileFromDisk = false)
    {
        if (deleteFileFromDisk && !string.IsNullOrEmpty(item.OutputPath))
        {
            try
            {
                if (File.Exists(item.OutputPath))
                {
                    File.Delete(item.OutputPath);
                }
                else if (Directory.Exists(item.OutputPath))
                {
                    Directory.Delete(item.OutputPath, recursive: true);
                }
            }
            catch { }
        }
        Data.History.Remove(item);
        Save();
    }

    public static void ClearHistory()
    {
        Data.History.Clear();
        Save();
    }

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegPath, false);
            return key?.GetValue(RunValueName) is string s && !string.IsNullOrEmpty(s);
        }
        catch { return false; }
    }

    public static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegPath, true);
            if (key == null) return;
            if (enable)
            {
                string exe = Environment.ProcessPath ?? "";
                if (!string.IsNullOrEmpty(exe))
                {
                    key.SetValue(RunValueName, $"\"{exe}\" --tray");
                }
            }
            else
            {
                key.DeleteValue(RunValueName, false);
            }
        }
        catch { }
    }

    public static bool IsContextMenuEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(FileShellKeyPath, false);
            return key != null;
        }
        catch { return false; }
    }

    public static void SetContextMenu(bool enable)
    {
        try
        {
            if (enable)
            {
                string exe = Environment.ProcessPath ?? "";
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;

                using (var fileKey = Registry.CurrentUser.CreateSubKey(FileShellKeyPath))
                {
                    fileKey.SetValue("", Loc.T("⚡ Конвертировать (ShiftConvert)", "⚡ Convert with ShiftConvert", "⚡ ShiftConvert ile Dönüştür"));
                    fileKey.SetValue("Icon", $"\"{exe}\",0");
                    using var cmdKey = fileKey.CreateSubKey("command");
                    cmdKey.SetValue("", $"\"{exe}\" --wheel \"%1\"");
                }

                using (var dirKey = Registry.CurrentUser.CreateSubKey(DirShellKeyPath))
                {
                    dirKey.SetValue("", Loc.T("⚡ Упаковать в ZIP (ShiftConvert)", "⚡ Pack to ZIP (ShiftConvert)", "⚡ ZIP'e Paketle (ShiftConvert)"));
                    dirKey.SetValue("Icon", $"\"{exe}\",0");
                    using var cmdKey = dirKey.CreateSubKey("command");
                    cmdKey.SetValue("", $"\"{exe}\" --wheel \"%1\"");
                }
            }
            else
            {
                Registry.CurrentUser.DeleteSubKeyTree(FileShellKeyPath, false);
                Registry.CurrentUser.DeleteSubKeyTree(DirShellKeyPath, false);
            }
        }
        catch { }
    }
}

public static class Loc
{
    public static string T(string ru, string en, string tr) => AppConfig.Data.Lang switch
    {
        "en" => en,
        "tr" => tr,
        _ => ru
    };
}

public static class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const int NIM_ADD = 0x00000000;
    public const int NIM_DELETE = 0x00000002;
    public const int NIF_MESSAGE = 0x00000001;
    public const int NIF_ICON = 0x00000002;
    public const int NIF_TIP = 0x00000004;
    public const int WM_TRAYICON = 0x8000 + 77;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_LBUTTONDBLCLK = 0x0203;
    public const int WM_RBUTTONUP = 0x0205;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr ExtractAssociatedIcon(IntPtr hInst, string lpIconPath, ref ushort lpiIcon);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}

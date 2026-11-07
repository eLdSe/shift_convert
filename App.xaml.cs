using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace ShiftConvertApp;

public partial class App : Application
{
    private static Mutex? _mutex;
    public static HubWindow? Hub { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
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

        AppDomain.CurrentDomain.UnhandledException += (_, _) => { };
        DispatcherUnhandledException += (_, ev) => { ev.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, ev) => { ev.SetObserved(); };

        // Очистка старых файлов после обновления
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
        });

        AppConfig.Load();
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        base.OnStartup(e);

        Hub = new HubWindow();
        GlobalDragHook.Initialize();

        // Если запущено не с флагом --silent (например, вручную пользователем) — показываем компактное окно
        bool silent = e.Args.Length > 0 && string.Equals(e.Args[0], "--tray", StringComparison.OrdinalIgnoreCase);
        if (!silent)
        {
            Hub.ShowHub();
        }

        MemoryOptimizer.ScheduleTrim(1200);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        GlobalDragHook.Shutdown();
        try { Hub?.DisposeTray(); } catch { }
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
            if (now - _lastTrimTicks < 1500) return;
            _lastTrimTicks = now;

            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();

            IntPtr handle = Process.GetCurrentProcess().Handle;
            EmptyWorkingSet(handle);
            SetProcessWorkingSetSize(handle, new IntPtr(-1), new IntPtr(-1));
        }
        catch { }
    }

    public static void ScheduleTrim(int delayMs = 1800)
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
    public int TotalConvertedCount { get; set; } = 0;
    public long TotalSavedBytes { get; set; } = 0;
    public List<HistoryEntry> History { get; set; } = new();
}

public static class AppConfig
{
    private const string RunRegPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ShiftConvert";

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

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}

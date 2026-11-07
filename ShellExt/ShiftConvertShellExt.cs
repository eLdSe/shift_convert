using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;

[assembly: AssemblyTitle("ShiftConvert Explorer Shell Extension")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: ComVisible(true)]

namespace ShiftConvertShellExt
{
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E8-0000-0000-C000-000000000046")]
    public interface IShellExtInit
    {
        [PreserveSig]
        int Initialize(IntPtr pidlFolder, IntPtr pDataObj, IntPtr hKeyProgID);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    public interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(IntPtr pici);

        [PreserveSig]
        int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    [Guid("E7D4A8B2-9C1F-4E5A-8B3D-1F2E3D4C5B6A")]
    public class ExplorerDragExtension : IShellExtInit, IContextMenu
    {
        private static int _watcherStarted;
        private static Mutex _watcherMutex;
        private static ExplorerDragExtension _lockedInstance;
        private static string[] _lastInitializedFiles = new string[0];
        private static long _lastInitTicks;

        static ExplorerDragExtension()
        {
            EnsureWatcherRunning();
        }

        public ExplorerDragExtension()
        {
            try
            {
                if (_lockedInstance == null)
                {
                    _lockedInstance = this;
                    CoLockObjectExternal(this, true, true);
                }
            }
            catch { }
            EnsureWatcherRunning();
        }

        public int Initialize(IntPtr pidlFolder, IntPtr pDataObj, IntPtr hKeyProgID)
        {
            EnsureWatcherRunning();
            try
            {
                if (pDataObj != IntPtr.Zero)
                {
                    var files = ExtractFilesFromDataObject(pDataObj);
                    if (files.Length > 0)
                    {
                        _lastInitializedFiles = files;
                        _lastInitTicks = Environment.TickCount;
                    }
                }
            }
            catch { }
            return 0; // S_OK
        }

        public int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags)
        {
            EnsureWatcherRunning();
            return 0; // S_OK, 0 items added
        }

        public int InvokeCommand(IntPtr pici)
        {
            return 0;
        }

        public int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax)
        {
            return -2147467263; // E_NOTIMPL
        }

        public static void EnsureWatcherRunning()
        {
            if (Interlocked.CompareExchange(ref _watcherStarted, 1, 0) != 0)
                return;

            try
            {
                var thread = new Thread(WatcherLoop)
                {
                    IsBackground = true,
                    Name = "ShiftConvert_ExplorerDragWatcher",
                    Priority = ThreadPriority.BelowNormal
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
            catch
            {
                Interlocked.Exchange(ref _watcherStarted, 0);
            }
        }

        private static void WatcherLoop()
        {
            bool createdNew = false;
            try
            {
                _watcherMutex = new Mutex(true, @"Local\ShiftConvert_ExplorerWatcher_v1", out createdNew);
            }
            catch
            {
                createdNew = true;
            }

            if (!createdNew)
                return;

            bool lButtonDown = false;
            bool shiftHeldAtStart = false;
            bool thresholdMet = false;
            bool triggeredThisDrag = false;
            POINT startPt = new POINT();
            long lastTriggerTicks = 0;

            while (true)
            {
                try
                {
                    bool isLDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
                    if (!isLDown)
                    {
                        lButtonDown = false;
                        thresholdMet = false;
                        triggeredThisDrag = false;
                        Thread.Sleep(35);
                        continue;
                    }

                    if (!lButtonDown)
                    {
                        lButtonDown = true;
                        GetCursorPos(out startPt);
                        shiftHeldAtStart = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
                        thresholdMet = false;
                        triggeredThisDrag = false;
                        Thread.Sleep(20);
                        continue;
                    }

                    POINT curPt;
                    GetCursorPos(out curPt);

                    if (!thresholdMet)
                    {
                        int dx = curPt.X - startPt.X;
                        int dy = curPt.Y - startPt.Y;
                        if (dx * dx + dy * dy >= 256) // 16px drag distance
                        {
                            thresholdMet = true;
                        }
                    }

                    if (thresholdMet && !shiftHeldAtStart && !triggeredThisDrag)
                    {
                        bool isShiftDown = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
                        if (isShiftDown)
                        {
                            triggeredThisDrag = true;
                            long now = Environment.TickCount;
                            if (now - lastTriggerTicks > 500)
                            {
                                lastTriggerTicks = now;
                                TryTriggerWheelOnDrag(startPt, curPt);
                            }
                        }
                    }

                    Thread.Sleep(18);
                }
                catch
                {
                    Thread.Sleep(100);
                }
            }
        }

        private static void TryTriggerWheelOnDrag(POINT startPt, POINT curPt)
        {
            try
            {
                // Если запущен основной ShiftConvert или запущен QuickNotch — они сами обрабатывают хук, не дублируем вызов
                Mutex existingMutex;
                if (Mutex.TryOpenExisting(@"Local\ShiftConvert_SingleInstance_v1", out existingMutex))
                {
                    existingMutex.Dispose();
                    return;
                }
                if (Mutex.TryOpenExisting(@"Local\QuickNotch_AppMutex_v2", out existingMutex))
                {
                    existingMutex.Dispose();
                    return;
                }

                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string cfgDir = Path.Combine(appData, "ShiftConvert");
                string exePathFile = Path.Combine(cfgDir, "shellext_target.txt");
                if (!File.Exists(exePathFile))
                    return;

                string exePath = File.ReadAllText(exePathFile, Encoding.UTF8).Trim();
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                    return;

                string cfgJsonPath = Path.Combine(cfgDir, "config.json");
                if (File.Exists(cfgJsonPath))
                {
                    string json = File.ReadAllText(cfgJsonPath, Encoding.UTF8);
                    if (json.IndexOf("\"HoldShiftEnabled\": false", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        json.IndexOf("\"HoldShiftEnabled\":false", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return;
                    }
                }

                string[] selectedFiles = GetSelectedExplorerFiles(startPt);
                if (selectedFiles.Length == 0 && _lastInitializedFiles.Length > 0 && (Environment.TickCount - _lastInitTicks) < 4000)
                {
                    selectedFiles = _lastInitializedFiles;
                }

                if (selectedFiles.Length > 0)
                {
                    // Отменяем нативное перемещение файла в Проводнике (Esc), чтобы файл случайно не переместился в другую папку
                    keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                }

                var argsBuilder = new StringBuilder("--drag-wheel");
                for (int i = 0; i < selectedFiles.Length; i++)
                {
                    argsBuilder.Append(" \"");
                    argsBuilder.Append(selectedFiles[i]);
                    argsBuilder.Append("\"");
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = argsBuilder.ToString(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? ""
                };
                Process.Start(psi);
            }
            catch { }
        }

        private static string[] GetSelectedExplorerFiles(POINT startPt)
        {
            var result = new List<string>();
            try
            {
                IntPtr fgHwnd = GetForegroundWindow();
                IntPtr rootFg = fgHwnd != IntPtr.Zero ? GetAncestor(fgHwnd, GA_ROOT) : IntPtr.Zero;

                IntPtr ptHwnd = WindowFromPoint(startPt);
                IntPtr rootPt = ptHwnd != IntPtr.Zero ? GetAncestor(ptHwnd, GA_ROOT) : IntPtr.Zero;

                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return result.ToArray();

                object shell = Activator.CreateInstance(shellType);
                object windows = shellType.InvokeMember("Windows", BindingFlags.InvokeMethod, null, shell, null);
                if (windows == null) return result.ToArray();

                int count = Convert.ToInt32(windows.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, windows, null));

                // 1. Проверяем окно Проводника под точкой начала перетаскивания или активное окно
                for (int i = 0; i < count; i++)
                {
                    object item = windows.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, windows, new object[] { i });
                    if (item == null) continue;

                    try
                    {
                        long itemHwnd = Convert.ToInt64(item.GetType().InvokeMember("HWND", BindingFlags.GetProperty, null, item, null));
                        if ((rootPt != IntPtr.Zero && itemHwnd == rootPt.ToInt64()) ||
                            (rootFg != IntPtr.Zero && itemHwnd == rootFg.ToInt64()) ||
                            (fgHwnd != IntPtr.Zero && itemHwnd == fgHwnd.ToInt64()))
                        {
                            ExtractSelectedItemsFromBrowser(item, result);
                            if (result.Count > 0) return result.ToArray();
                        }
                    }
                    catch { }
                }

                // 2. Проверяем Рабочий стол (Progman / WorkerW / SysListView32 / SHELLDLL_DefView)
                string clsFg = GetWindowClass(rootFg != IntPtr.Zero ? rootFg : fgHwnd);
                string clsPt = GetWindowClass(rootPt != IntPtr.Zero ? rootPt : ptHwnd);
                if (IsDesktopClass(clsFg) || IsDesktopClass(clsPt))
                {
                    try
                    {
                        object pvarLoc = 0;
                        object pvarLocRoot = 0;
                        int phwnd = 0;
                        object desktopDisp = windows.GetType().InvokeMember(
                            "FindWindowSW",
                            BindingFlags.InvokeMethod,
                            null,
                            windows,
                            new object[] { pvarLoc, pvarLocRoot, 8 /* SWC_DESKTOP */, phwnd, 1 /* SWFO_NEEDDISPATCH */ });
                        if (desktopDisp != null)
                        {
                            ExtractSelectedItemsFromBrowser(desktopDisp, result);
                            if (result.Count > 0) return result.ToArray();
                        }
                    }
                    catch { }
                }

                // 3. Фолбэк по открытым окнам Explorer
                for (int i = 0; i < count; i++)
                {
                    object item = windows.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, windows, new object[] { i });
                    if (item == null) continue;
                    ExtractSelectedItemsFromBrowser(item, result);
                    if (result.Count > 0) return result.ToArray();
                }
            }
            catch { }
            return result.ToArray();
        }

        private static bool IsDesktopClass(string cls)
        {
            return cls == "Progman" || cls == "WorkerW" || cls == "SysListView32" || cls == "SHELLDLL_DefView";
        }

        private static void ExtractSelectedItemsFromBrowser(object browserItem, List<string> output)
        {
            try
            {
                object doc = browserItem.GetType().InvokeMember("Document", BindingFlags.GetProperty, null, browserItem, null);
                if (doc == null) return;

                object selectedItems = doc.GetType().InvokeMember("SelectedItems", BindingFlags.InvokeMethod, null, doc, null);
                if (selectedItems == null) return;

                int selCount = Convert.ToInt32(selectedItems.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, selectedItems, null));
                for (int j = 0; j < selCount; j++)
                {
                    object fileItem = selectedItems.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, selectedItems, new object[] { j });
                    if (fileItem == null) continue;

                    string path = Convert.ToString(fileItem.GetType().InvokeMember("Path", BindingFlags.GetProperty, null, fileItem, null));
                    if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                    {
                        output.Add(path);
                    }
                }
            }
            catch { }
        }

        private static string[] ExtractFilesFromDataObject(IntPtr pDataObj)
        {
            var list = new List<string>();
            try
            {
                var dataObj = (IDataObject)Marshal.GetObjectForIUnknown(pDataObj);
                var fmt = new FORMATETC
                {
                    cfFormat = 15, // CF_HDROP
                    ptd = IntPtr.Zero,
                    dwAspect = DVASPECT.DVASPECT_CONTENT,
                    lindex = -1,
                    tymed = TYMED.TYMED_HGLOBAL
                };
                STGMEDIUM medium;
                dataObj.GetData(ref fmt, out medium);
                if (medium.unionmember != IntPtr.Zero)
                {
                    try
                    {
                        uint count = DragQueryFile(medium.unionmember, 0xFFFFFFFF, null, 0);
                        for (uint i = 0; i < count; i++)
                        {
                            var sb = new StringBuilder(1024);
                            if (DragQueryFile(medium.unionmember, i, sb, (uint)sb.Capacity) > 0)
                            {
                                string p = sb.ToString();
                                if (File.Exists(p) || Directory.Exists(p))
                                    list.Add(p);
                            }
                        }
                    }
                    finally
                    {
                        ReleaseStgMedium(ref medium);
                    }
                }
            }
            catch { }
            return list.ToArray();
        }

        private static string GetWindowClass(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            var sb = new StringBuilder(128);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private const int VK_LBUTTON = 0x01;
        private const int VK_SHIFT = 0x10;
        private const byte VK_ESCAPE = 0x1B;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint GA_ROOT = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT Point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder lpszFile, uint cch);

        [DllImport("ole32.dll")]
        private static extern void ReleaseStgMedium(ref STGMEDIUM pmedium);

        [DllImport("ole32.dll")]
        private static extern int CoLockObjectExternal([MarshalAs(UnmanagedType.IUnknown)] object pUnk, bool fLock, bool fLastUnlockReleases);
    }
}

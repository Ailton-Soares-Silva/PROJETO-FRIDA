// language: C#, file: FridaWindowVerification.cs, runtime: .NET Framework 4.8, target: Windows 11
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;

namespace FridaAgent
{
    public sealed class WindowEntry
    {
        public IntPtr Hwnd;
        public int Pid;
        public string ProcessName;
        public string Title;
        public List<string> Tabs = new List<string>();
    }

    public static class FridaWindowVerification
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr OpenWindowStation(string lpszWinSta, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetProcessWindowStation(IntPtr hWinSta);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetThreadDesktop(IntPtr hDesktop);

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        static extern bool IsHungAppWindow(IntPtr hWnd);

        const uint WINSTA_ALL = 0x37F;
        const uint DESKTOP_ALL = 0x1FF;

        public static void EnsureInteractiveDesktop()
        {
            try
            {
                IntPtr hwinsta = OpenWindowStation("winsta0", false, WINSTA_ALL);
                if (hwinsta != IntPtr.Zero)
                {
                    SetProcessWindowStation(hwinsta);
                    IntPtr hdesk = OpenDesktop("default", 0, false, DESKTOP_ALL);
                    if (hdesk != IntPtr.Zero) SetThreadDesktop(hdesk);
                }
            }
            catch { }
        }

        public static List<WindowEntry> GetActiveWindows(string filter = null, bool readTabs = true)
        {
            EnsureInteractiveDesktop();
            var list = new List<WindowEntry>();
            try
            {
                EnumWindows((hWnd, lParam) =>
                {
                    try
                    {
                        if (IsWindowVisible(hWnd))
                        {
                            int len = GetWindowTextLength(hWnd);
                            if (len > 0)
                            {
                                var sb = new StringBuilder(len + 1);
                                GetWindowText(hWnd, sb, sb.Capacity);
                                string title = sb.ToString();
                                uint pid;
                                GetWindowThreadProcessId(hWnd, out pid);
                                string procName = "unknown";
                                try { procName = Process.GetProcessById((int)pid).ProcessName; } catch { }

                                var entry = new WindowEntry
                                {
                                    Hwnd = hWnd,
                                    Pid = (int)pid,
                                    ProcessName = procName,
                                    Title = title
                                };

                                string pLower = procName.ToLowerInvariant();
                                if (readTabs && (pLower == "chrome" || pLower == "msedge" || pLower == "brave" || pLower == "opera"))
                                {
                                    ExtractBrowserTabs(entry);
                                }

                                if (string.IsNullOrWhiteSpace(filter) || Matches(entry, filter))
                                {
                                    list.Add(entry);
                                }
                            }
                        }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return list;
        }

        private static void ExtractBrowserTabs(WindowEntry entry)
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(entry.Hwnd);
                if (root != null)
                {
                    var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
                    var tabs = root.FindAll(TreeScope.Descendants, condition);
                    for (int i = 0; i < tabs.Count; i++)
                    {
                        try
                        {
                            string tabName = tabs[i].Current.Name;
                            if (!string.IsNullOrWhiteSpace(tabName) && !entry.Tabs.Contains(tabName))
                                entry.Tabs.Add(tabName);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        public static bool WaitForWindowOrTab(string target, int timeoutMs, out string matchedEvidence)
        {
            matchedEvidence = null;
            if (string.IsNullOrWhiteSpace(target)) return false;

            string keyword = CleanTargetSearchToken(target);
            int dynamicTimeout = timeoutMs;
            string tLower = target.ToLowerInvariant();
            if (tLower.Contains("solidworks") || tLower.Contains("autocad") || tLower.Contains("inventor") || tLower.Contains("matlab"))
            {
                dynamicTimeout = Math.Max(dynamicTimeout, 5000);
            }

            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < dynamicTimeout)
            {
                var windows = GetActiveWindows(keyword, true);
                if (windows.Count > 0)
                {
                    var w = windows[0];
                    matchedEvidence = "[" + w.ProcessName + "] " + w.Title;
                    if (w.Tabs.Count > 0)
                        matchedEvidence += " (Abas: " + string.Join(", ", w.Tabs.ToArray()) + ")";
                    return true;
                }
                Thread.Sleep(250);
            }
            return false;
        }

        private static bool Matches(WindowEntry e, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return true;
            string[] tokens = filter.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            string titleLower = (e.Title ?? "").ToLowerInvariant();
            string procLower = (e.ProcessName ?? "").ToLowerInvariant();

            foreach (var tok in tokens)
            {
                string t = tok.Trim().ToLowerInvariant();
                if (t.Length < 2) continue;
                if (titleLower.Contains(t) || procLower.Contains(t)) return true;
                foreach (var tab in e.Tabs)
                {
                    if (tab.ToLowerInvariant().Contains(t)) return true;
                }
            }
            return false;
        }

        private static string CleanTargetSearchToken(string target)
        {
            string t = (target ?? "").Trim().Trim('"', '\'');
            if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Uri u = new Uri(t);
                    if (u.Host.Contains("docs.google.com") || u.Host.Contains("drive.google.com"))
                    {
                        // Se for planilha/doc do Google, o titulo da aba no Chrome contera o nome do arquivo ou "Google"
                        return "google";
                    }
                    string host = u.Host.Replace("www.", "");
                    string[] parts = host.Split('.');
                    return parts.Length > 0 ? parts[0] : host;
                }
                catch { }
            }
            string fname = Path.GetFileNameWithoutExtension(t);
            return string.IsNullOrWhiteSpace(fname) ? t : fname;
        }

        public static FridaToolReceipt ExecuteReceipt(Dictionary<string, object> args)
        {
            string filter = args != null && args.ContainsKey("filter") ? Convert.ToString(args["filter"]) : "";
            bool includeTabs = true;
            if (args != null && args.ContainsKey("include_tabs"))
                bool.TryParse(Convert.ToString(args["include_tabs"]), out includeTabs);

            var receipt = new FridaToolReceipt("inspect_active_windows", "inspect", filter, false);
            var windows = GetActiveWindows(filter, includeTabs);
            var simplified = new List<Dictionary<string, object>>();
            foreach (var w in windows)
            {
                var d = new Dictionary<string, object>();
                d["pid"] = w.Pid;
                d["process"] = w.ProcessName;
                d["title"] = w.Title;
                if (w.Tabs.Count > 0) d["tabs"] = w.Tabs;
                simplified.Add(d);
            }

            string json = new JavaScriptSerializer().Serialize(simplified);
            receipt.Put("windows_count", windows.Count);
            receipt.Put("snapshot", json);
            receipt.Check("desktop_windows_queried", true, "enumerated", "found " + windows.Count, "win32_uiautomation");

            return receipt.Finish(FridaResultStatus.Verified, "windows_enumerated", "Janelas ativas e abas de navegadores inspecionadas (" + windows.Count + " encontradas).", json);
        }
    }
}

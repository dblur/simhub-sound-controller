using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace User.AppVolumeController
{
    internal static class MediaControl
    {
        private const uint WmAppCommand = 0x0319;
        private const int AppCommandMediaNextTrack = 11;
        private const int AppCommandMediaPreviousTrack = 12;
        private const int AppCommandMediaPlayPause = 14;
        private const uint SmtoAbortIfHung = 0x0002;
        private const uint ProcessQueryLimitedInformation = 0x1000;

        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "desktop", "windows", "microsoft", "program", "programs", "users", "local",
            "appdata", "application", "system", "default", "files", "roaming", "software"
        };

        private static readonly object Gate = new object();
        private static GlobalSystemMediaTransportControlsSessionManager _manager;

        public static bool Toggle(VolumeChannel channel)
        {
            return Send(channel, AppCommandMediaPlayPause, session => session.TryTogglePlayPauseAsync().AsTask());
        }

        public static bool Next(VolumeChannel channel)
        {
            return Send(channel, AppCommandMediaNextTrack, session => session.TrySkipNextAsync().AsTask());
        }

        public static bool Previous(VolumeChannel channel)
        {
            return Send(channel, AppCommandMediaPreviousTrack, session => session.TrySkipPreviousAsync().AsTask());
        }

        private static bool Send(
            VolumeChannel channel,
            int commandId,
            Func<GlobalSystemMediaTransportControlsSession, Task<bool>> command)
        {
            var targets = ReadTargets(channel);
            if (targets.Count == 0)
                return false;

            bool? sessionResult = null;
            try
            {
                var task = Task.Run(() => SendCore(targets, command));
                if (task.Wait(2500))
                    sessionResult = task.Result;
            }
            catch
            {
                sessionResult = null;
            }

            if (sessionResult == true)
                return true;
            if (sessionResult == false)
                return false;

            return SendToWindow(targets, commandId);
        }

        private static async Task<bool?> SendCore(
            List<MediaTarget> targets,
            Func<GlobalSystemMediaTransportControlsSession, Task<bool>> command)
        {
            var manager = Manager();
            if (manager == null)
                return null;

            var identities = ProcessIdentities(targets);
            GlobalSystemMediaTransportControlsSession match = null;
            int bestScore = 0;
            foreach (var session in manager.GetSessions())
            {
                int score = Score(session, identities);
                if (score > bestScore)
                {
                    bestScore = score;
                    match = session;
                }
            }

            if (match == null)
                return null;

            return await command(match).ConfigureAwait(false);
        }

        private static GlobalSystemMediaTransportControlsSessionManager Manager()
        {
            lock (Gate)
            {
                if (_manager != null)
                    return _manager;

                try
                {
                    _manager = GlobalSystemMediaTransportControlsSessionManager.RequestAsync()
                        .AsTask()
                        .ConfigureAwait(false)
                        .GetAwaiter()
                        .GetResult();
                }
                catch
                {
                    _manager = null;
                }

                return _manager;
            }
        }

        private static int Score(GlobalSystemMediaTransportControlsSession session, List<ProcessIdentity> identities)
        {
            string appId = "";
            try
            {
                appId = session.SourceAppUserModelId ?? "";
            }
            catch
            {
                return 0;
            }

            if (appId.Length == 0 || identities.Count == 0)
                return 0;

            string compactId = Compact(appId);
            int best = 0;
            foreach (var identity in identities)
            {
                int score = 0;
                if (identity.AppId.Length > 0 && string.Equals(appId, identity.AppId, StringComparison.OrdinalIgnoreCase))
                {
                    score = 100;
                }
                else
                {
                    var shared = identity.Tokens.Where(token => compactId.Contains(token)).ToList();
                    bool confident = shared.Count >= 2 || shared.Any(token => token.Length >= 7);
                    if (!confident)
                        continue;
                    score = 40 + shared.Count * 10;
                }

                try
                {
                    var status = session.GetPlaybackInfo()?.PlaybackStatus;
                    if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        score += 1;
                }
                catch
                {
                }

                if (score > best)
                    best = score;
            }

            return best;
        }

        private static List<ProcessIdentity> ProcessIdentities(List<MediaTarget> targets)
        {
            var result = new List<ProcessIdentity>();
            var seen = new HashSet<int>();
            foreach (var target in targets)
            {
                if (target.ProcessName.Length == 0)
                    continue;

                Process[] processes;
                try
                {
                    processes = Process.GetProcessesByName(target.ProcessName);
                }
                catch
                {
                    continue;
                }

                foreach (var process in processes)
                {
                    try
                    {
                        if (!seen.Add(process.Id))
                            continue;

                        string path = QueryProcessPath(process.Id);
                        var tokens = new HashSet<string>(StringComparer.Ordinal);
                        AddTokens(tokens, target.ProcessName);
                        AddTokens(tokens, Path.GetFileNameWithoutExtension(path));
                        AddTokens(tokens, Path.GetFileName(Path.GetDirectoryName(path) ?? ""));
                        try
                        {
                            if (path.Length > 0)
                            {
                                var info = FileVersionInfo.GetVersionInfo(path);
                                AddTokens(tokens, info.ProductName);
                                AddTokens(tokens, info.FileDescription);
                            }
                        }
                        catch
                        {
                        }

                        result.Add(new ProcessIdentity
                        {
                            Tokens = tokens,
                            AppId = ReadAppId(process.Id)
                        });
                    }
                    catch
                    {
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }

            return result;
        }

        private static void AddTokens(HashSet<string> tokens, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            string spaced = Regex.Replace(value, "([\\p{Ll}])([\\p{Lu}])", "$1 $2");
            foreach (var part in Regex.Split(spaced.ToLowerInvariant(), @"[^\p{L}\p{Nd}]+"))
            {
                if (part.Length >= 4 && !StopWords.Contains(part))
                    tokens.Add(part);
            }
        }

        private static string QueryProcessPath(int processId)
        {
            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero)
                return "";

            try
            {
                int size = 1024;
                var buffer = new StringBuilder(size);
                if (!QueryFullProcessImageName(handle, 0, buffer, ref size))
                    return "";
                return buffer.ToString();
            }
            catch
            {
                return "";
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static string ReadAppId(int processId)
        {
            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero)
                return "";

            try
            {
                uint length = 1024;
                var buffer = new StringBuilder(1024);
                if (GetApplicationUserModelId(handle, ref length, buffer) != 0)
                    return "";
                return buffer.ToString();
            }
            catch
            {
                return "";
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static string Compact(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            var chars = value.Where(char.IsLetterOrDigit).ToArray();
            return new string(chars).ToLowerInvariant();
        }

        private static bool SendToWindow(List<MediaTarget> targets, int commandId)
        {
            try
            {
                IntPtr hwnd = FindWindow(targets);
                if (hwnd == IntPtr.Zero)
                    return false;

                IntPtr command = (IntPtr)(commandId << 16);
                SendMessageTimeout(hwnd, WmAppCommand, hwnd, command, SmtoAbortIfHung, 200, out _);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static IntPtr FindWindow(List<MediaTarget> targets)
        {
            IntPtr match = IntPtr.Zero;
            var names = new Dictionary<int, string>();
            EnumWindows((hwnd, lParam) =>
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0)
                    return true;

                int processId = (int)pid;
                if (!names.TryGetValue(processId, out string processName))
                {
                    processName = ProcessName(processId);
                    names[processId] = processName;
                }

                if (string.IsNullOrEmpty(processName))
                    return true;

                string title = WindowTitle(hwnd);
                foreach (var target in targets)
                {
                    if (!string.Equals(AudioSessionService.NormalizeProcess(processName), target.ProcessName, StringComparison.Ordinal))
                        continue;
                    if (!string.IsNullOrEmpty(target.TitleFilter) &&
                        title.IndexOf(target.TitleFilter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    match = hwnd;
                    if (IsWindowVisible(hwnd))
                        return false;
                }

                return true;
            }, IntPtr.Zero);

            return match;
        }

        private static List<MediaTarget> ReadTargets(VolumeChannel channel)
        {
            var result = new List<MediaTarget>();
            if (channel?.Targets == null)
                return result;

            foreach (var target in channel.Targets.ToArray())
            {
                if (target == null)
                    continue;
                string process = AudioSessionService.NormalizeProcess(target.ProcessName);
                if (process.Length == 0)
                    continue;
                result.Add(new MediaTarget
                {
                    ProcessName = process,
                    TitleFilter = (target.WindowTitleFilter ?? "").Trim()
                });
            }

            return result;
        }

        private static string ProcessName(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                    return process.ProcessName ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string WindowTitle(IntPtr hwnd)
        {
            var text = new StringBuilder(512);
            if (GetWindowText(hwnd, text, text.Capacity) <= 0)
                return "";
            return text.ToString();
        }

        private sealed class MediaTarget
        {
            public string ProcessName;
            public string TitleFilter;
        }

        private sealed class ProcessIdentity
        {
            public HashSet<string> Tokens = new HashSet<string>(StringComparer.Ordinal);
            public string AppId = "";
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetApplicationUserModelId(IntPtr hProcess, ref uint applicationUserModelIdLength, StringBuilder applicationUserModelId);
    }
}

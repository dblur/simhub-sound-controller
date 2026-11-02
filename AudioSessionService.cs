using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;

namespace User.AppVolumeController
{
    public sealed class ChannelSnapshot
    {
        public bool Found;
        public int Volume;
        public bool Muted;
        public double Peak;
        public string ProcessName = "";
    }

    public sealed class AppCandidate
    {
        public string ProcessName { get; set; } = "";
        public string WindowTitle { get; set; } = "";
        public int ProcessId { get; set; }
        public int Volume { get; set; }
        public bool HasAudio { get; set; }
        public string Kind { get; set; } = "Window";

        public string Secondary
        {
            get
            {
                var title = string.IsNullOrWhiteSpace(WindowTitle) ? "Без заголовка" : WindowTitle;
                return HasAudio ? title + "  ·  " + Volume + "%" : title;
            }
        }
    }

    internal sealed class SessionRecord
    {
        public int ProcessId;
        public string ProcessName = "";
        public float Volume;
        public bool Muted;
        public float Peak;
        public SimpleAudioVolume AudioVolume;
        public List<string> WindowTitles = new List<string>();
    }

    public sealed class AudioSessionService : IDisposable
    {
        private readonly object _sync = new object();
        private readonly object _presenceLock = new object();
        private HashSet<string> _runningNames;
        private int _runningStamp = int.MinValue;

        public ChannelSnapshot Query(VolumeChannel channel)
        {
            return UseSessions(sessions => Snapshot(Match(channel, sessions)));
        }

        public bool IsAppOpen(VolumeChannel channel)
        {
            if (channel?.Targets == null || channel.Targets.Count == 0)
                return false;

            HashSet<string> names;
            lock (_presenceLock)
                names = RunningProcessNames();

            List<WindowInfo> windows = null;
            foreach (var target in channel.Targets.ToArray())
            {
                if (target == null)
                    continue;

                string process = NormalizeProcess(target.ProcessName);
                string title = (target.WindowTitleFilter ?? "").Trim();
                if (process.Length == 0 && title.Length == 0)
                    continue;

                if (process.Length > 0 && names.Contains(process))
                    return true;

                if (title.Length == 0)
                    continue;

                if (windows == null)
                    windows = WindowEnumerator.GetVisibleWindows();

                foreach (var window in windows)
                {
                    if (process.Length > 0 && NormalizeProcess(window.ProcessName) != process)
                        continue;
                    if (window.Title.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }

            return false;
        }

        private HashSet<string> RunningProcessNames()
        {
            int now = Environment.TickCount;
            if (_runningNames != null && unchecked(now - _runningStamp) < 350)
                return _runningNames;

            var names = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        string name = NormalizeProcess(process.ProcessName);
                        if (name.Length > 0)
                            names.Add(name);
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
            catch
            {
            }

            _runningNames = names;
            _runningStamp = now;
            return names;
        }

        public ChannelSnapshot AdjustVolume(VolumeChannel channel, int deltaPercent)
        {
            return UseSessions(sessions =>
            {
                var matched = Match(channel, sessions);
                int current = matched.Count > 0
                    ? (int)Math.Round(matched.Average(s => s.Volume) * 100.0)
                    : channel.CurrentVolume;
                return SetMatchedVolume(matched, current + deltaPercent);
            });
        }

        public ChannelSnapshot SetVolume(VolumeChannel channel, int percent)
        {
            return UseSessions(sessions => SetMatchedVolume(Match(channel, sessions), percent));
        }

        public ChannelSnapshot SetMute(VolumeChannel channel, bool? mute)
        {
            return UseSessions(sessions =>
            {
                var matched = Match(channel, sessions);
                if (matched.Count == 0)
                    return new ChannelSnapshot { Found = false, Volume = channel.CurrentVolume, Muted = mute ?? !channel.IsMuted };

                bool next = mute ?? !matched.Any(s => s.Muted);
                foreach (var session in matched)
                {
                    try
                    {
                        if (session.AudioVolume != null)
                            session.AudioVolume.Mute = next;
                    }
                    catch
                    {
                    }
                }

                return Snapshot(matched);
            });
        }

        public List<AppCandidate> ListAudioApps()
        {
            return UseSessions(sessions =>
            {
                var windows = WindowEnumerator.GetVisibleWindows();
                var result = new List<AppCandidate>();
                foreach (var session in sessions
                    .GroupBy(s => NormalizeProcess(s.ProcessName) + "#" + s.ProcessId)
                    .Select(g => g.First()))
                {
                    if (string.IsNullOrWhiteSpace(session.ProcessName))
                        continue;

                    var title = windows
                        .Where(w => w.ProcessId == session.ProcessId && !string.IsNullOrWhiteSpace(w.Title))
                        .Select(w => w.Title)
                        .FirstOrDefault() ?? "";

                    result.Add(new AppCandidate
                    {
                        ProcessName = session.ProcessName,
                        WindowTitle = title,
                        ProcessId = session.ProcessId,
                        Volume = (int)Math.Round(session.Volume * 100.0),
                        HasAudio = true,
                        Kind = "Audio"
                    });
                }

                return result.OrderBy(a => a.ProcessName, StringComparer.OrdinalIgnoreCase).ToList();
            });
        }

        public List<AppCandidate> ListWindows()
        {
            var windows = WindowEnumerator.GetVisibleWindows();
            var result = new List<AppCandidate>();
            foreach (var window in windows)
            {
                if (string.IsNullOrWhiteSpace(window.Title))
                    continue;

                result.Add(new AppCandidate
                {
                    ProcessName = window.ProcessName,
                    WindowTitle = window.Title,
                    ProcessId = window.ProcessId,
                    HasAudio = false,
                    Kind = "Window"
                });
            }

            return result
                .OrderBy(a => a.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.WindowTitle, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public void Dispose()
        {
        }

        private ChannelSnapshot SetMatchedVolume(List<SessionRecord> matched, int percent)
        {
            percent = percent < 0 ? 0 : (percent > 100 ? 100 : percent);
            float volume = percent / 100f;
            foreach (var session in matched)
            {
                try
                {
                    if (session.AudioVolume != null)
                        session.AudioVolume.Volume = volume;
                }
                catch
                {
                }
            }

            var snapshot = Snapshot(matched);
            snapshot.Volume = percent;
            return snapshot;
        }

        private static ChannelSnapshot Snapshot(List<SessionRecord> matched)
        {
            if (matched == null || matched.Count == 0)
                return new ChannelSnapshot { Found = false };

            return new ChannelSnapshot
            {
                Found = true,
                Volume = (int)Math.Round(matched.Average(s => s.Volume) * 100.0),
                Muted = matched.All(s => s.Muted),
                Peak = matched.Max(s => (double)s.Peak),
                ProcessName = string.Join(", ", matched.Select(s => s.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase))
            };
        }

        private static List<SessionRecord> Match(VolumeChannel channel, List<SessionRecord> sessions)
        {
            var matched = new List<SessionRecord>();
            if (channel == null || !channel.Enabled || channel.Targets == null || channel.Targets.Count == 0)
                return matched;

            foreach (var session in sessions)
            {
                if (Matches(channel, session))
                    matched.Add(session);
            }

            return matched;
        }

        private static bool Matches(VolumeChannel channel, SessionRecord session)
        {
            foreach (var target in channel.Targets)
            {
                if (target == null)
                    continue;

                bool hasProcess = !string.IsNullOrWhiteSpace(target.ProcessName);
                bool hasTitle = !string.IsNullOrWhiteSpace(target.WindowTitleFilter);
                if (!hasProcess && !hasTitle)
                    continue;

                bool processOk = !hasProcess ||
                    NormalizeProcess(session.ProcessName) == NormalizeProcess(target.ProcessName);
                bool titleOk = !hasTitle ||
                    session.WindowTitles.Any(title =>
                        title.IndexOf(target.WindowTitleFilter, StringComparison.OrdinalIgnoreCase) >= 0);

                if (processOk && titleOk)
                    return true;
            }

            return false;
        }

        private T UseSessions<T>(Func<List<SessionRecord>, T> work)
        {
            lock (_sync)
            {
                var sessions = new List<SessionRecord>();
                var windowsByPid = WindowEnumerator.GetVisibleWindows()
                    .GroupBy(w => w.ProcessId)
                    .ToDictionary(g => g.Key, g => g.Select(w => w.Title).Where(t => !string.IsNullOrWhiteSpace(t)).ToList());

                try
                {
                    using (var enumerator = new MMDeviceEnumerator())
                    {
                        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                        foreach (var device in devices)
                        {
                            try
                            {
                                CollectDeviceSessions(device, sessions, windowsByPid);
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                catch
                {
                }

                return work(sessions);
            }
        }

        private static void CollectDeviceSessions(
            MMDevice device,
            List<SessionRecord> sessions,
            Dictionary<int, List<string>> windowsByPid)
        {
            if (device == null)
                return;

            try
            {
                device.AudioSessionManager.RefreshSessions();
            }
            catch
            {
            }

            var collection = device.AudioSessionManager.Sessions;
            if (collection == null)
                return;

            for (int i = 0; i < collection.Count; i++)
            {
                AudioSessionControl session = null;
                try
                {
                    session = collection[i];
                    if (session == null)
                        continue;
                    try
                    {
                        if (session.IsSystemSoundsSession)
                            continue;
                    }
                    catch
                    {
                    }

                    uint pid;
                    try
                    {
                        pid = session.GetProcessID;
                    }
                    catch
                    {
                        continue;
                    }

                    if (pid == 0)
                        continue;

                    int processId = (int)pid;
                    var record = new SessionRecord
                    {
                        ProcessId = processId,
                        AudioVolume = session.SimpleAudioVolume
                    };

                    try
                    {
                        if (session.SimpleAudioVolume != null)
                        {
                            record.Volume = session.SimpleAudioVolume.Volume;
                            record.Muted = session.SimpleAudioVolume.Mute;
                        }
                    }
                    catch
                    {
                    }

                    try
                    {
                        if (session.AudioMeterInformation != null)
                            record.Peak = session.AudioMeterInformation.MasterPeakValue;
                    }
                    catch
                    {
                    }

                    record.ProcessName = ResolveProcessName(processId, session);
                    if (windowsByPid.TryGetValue(processId, out var titles))
                        record.WindowTitles = titles;

                    if (!string.IsNullOrWhiteSpace(record.ProcessName))
                        sessions.Add(record);
                }
                catch
                {
                }
            }
        }

        private static string ResolveProcessName(int processId, AudioSessionControl session)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    if (!string.IsNullOrWhiteSpace(process.ProcessName))
                        return process.ProcessName;
                }
            }
            catch
            {
            }

            try
            {
                var id = session.GetSessionIdentifier;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    var exe = id.Split('|').LastOrDefault(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(exe))
                        return System.IO.Path.GetFileNameWithoutExtension(exe);
                }
            }
            catch
            {
            }

            return "";
        }

        internal static string NormalizeProcess(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "";

            name = name.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            return name.ToLowerInvariant();
        }
    }

    internal sealed class WindowInfo
    {
        public int ProcessId;
        public string ProcessName = "";
        public string Title = "";
    }

    internal static class WindowEnumerator
    {
        private const uint GwOwner = 4;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        public static List<WindowInfo> GetVisibleWindows()
        {
            var result = new List<WindowInfo>();
            try
            {
                EnumWindows((hWnd, lParam) =>
                {
                    try
                    {
                        if (!IsWindowVisible(hWnd))
                            return true;
                        if (GetWindow(hWnd, GwOwner) != IntPtr.Zero)
                            return true;

                        var text = new StringBuilder(512);
                        if (GetWindowText(hWnd, text, text.Capacity) <= 0)
                            return true;

                        GetWindowThreadProcessId(hWnd, out uint pid);
                        if (pid == 0)
                            return true;

                        result.Add(new WindowInfo
                        {
                            ProcessId = (int)pid,
                            Title = text.ToString(),
                            ProcessName = TryGetProcessName((int)pid)
                        });
                    }
                    catch
                    {
                    }

                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
            }

            return result;
        }

        private static string TryGetProcessName(int processId)
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
    }
}

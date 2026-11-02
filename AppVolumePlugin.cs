using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using SimHub.Plugins;

namespace User.AppVolumeController
{
    [PluginDescription("Громкость отдельных приложений с кнопок устройства и свойства для подсветки.")]
    [PluginAuthor("dmitblur")]
    [PluginName("App Volume Controller")]
    public class AppVolumePlugin : IPlugin, IWPFSettingsV2
    {
        private const int MuteHoldMs = 500;

        private readonly AudioSessionService _audio = new AudioSessionService();
        private readonly HashSet<int> _registeredSlots = new HashSet<int>();
        private readonly MuteHold[] _holds = new MuteHold[AppVolumeSettings.MaxSlots + 1];
        private System.Threading.Timer _refreshTimer;

        public AppVolumePlugin()
        {
            for (int i = 0; i < _holds.Length; i++)
                _holds[i] = new MuteHold();
        }

        public AppVolumeSettings Settings { get; private set; }

        public PluginManager PluginManager { get; set; }

        public ImageSource PictureIcon => this.ToIcon(CreateMenuIcon());

        public string LeftMenuTitle => "Громкость приложений";

        public void Init(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("App Volume Controller starting");

            Settings = this.ReadCommonSettings("GeneralSettings", () => new AppVolumeSettings());
            if (Settings == null)
                Settings = new AppVolumeSettings();
            Settings.Normalize();

            foreach (var channel in Settings.Channels)
                RegisterSlot(channel.Slot);

            RefreshAllChannels();
            _refreshTimer = new System.Threading.Timer(_ =>
            {
                try { RefreshAllChannels(); }
                catch { }
            }, null, 400, 400);
        }

        public void End(PluginManager pluginManager)
        {
            _refreshTimer?.Dispose();
            _refreshTimer = null;
            foreach (var hold in _holds)
            {
                lock (hold)
                {
                    hold.Timer?.Dispose();
                    hold.Timer = null;
                    hold.IsDown = false;
                }
            }
            SaveSettings();
            _audio.Dispose();
        }

        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new SettingsControl(this);
        }

        public void SaveSettings()
        {
            try
            {
                this.SaveCommonSettings("GeneralSettings", Settings);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error("App Volume Controller: failed to save settings: " + ex.Message);
            }
        }

        public VolumeChannel AddChannel()
        {
            if (Settings?.Channels == null)
                return null;

            int slot = AppVolumeSettings.FirstFreeSlot(Settings.Channels.Select(channel => channel.Slot));
            if (slot == 0)
                return null;

            RegisterSlot(slot);
            var channel = AppVolumeSettings.CreateChannel(slot);
            Settings.Channels.Add(channel);
            RefreshChannel(channel);
            SaveSettings();
            return channel;
        }

        public void RemoveChannel(VolumeChannel channel)
        {
            if (channel == null || Settings?.Channels == null)
                return;

            Settings.Channels.Remove(channel);
            SaveSettings();
        }

        public void RefreshAllChannels()
        {
            if (Settings?.Channels == null)
                return;

            foreach (var channel in Settings.Channels.ToArray())
                RefreshChannel(channel);
        }

        public void ChangeVolume(VolumeChannel channel, int deltaPercent)
        {
            if (channel == null || !channel.Enabled)
                return;

            int previous = channel.CurrentVolume;
            var snapshot = _audio.AdjustVolume(channel, deltaPercent);
            OnUi(() =>
            {
                ApplySnapshot(channel, snapshot, previous + deltaPercent, snapshot.Found || channel.IsRunning);
                if (channel.CurrentVolume != previous)
                    this.TriggerEvent("Channel" + channel.Slot + ".VolumeChanged");
            });
        }

        public void MuteButtonDown(VolumeChannel channel)
        {
            if (channel == null || channel.Slot < 1 || channel.Slot >= _holds.Length)
                return;

            var hold = _holds[channel.Slot];
            lock (hold)
            {
                if (hold.IsDown)
                    return;

                hold.IsDown = true;
                hold.LongFired = false;
                hold.Timer?.Dispose();
                int slot = channel.Slot;
                hold.Timer = new System.Threading.Timer(_ => OnMuteHeld(slot), null, MuteHoldMs, System.Threading.Timeout.Infinite);
            }
        }

        public void MuteButtonUp(VolumeChannel channel)
        {
            if (channel == null || channel.Slot < 1 || channel.Slot >= _holds.Length)
                return;

            var hold = _holds[channel.Slot];
            bool shortPress;
            lock (hold)
            {
                if (!hold.IsDown)
                    return;

                hold.IsDown = false;
                hold.Timer?.Dispose();
                hold.Timer = null;
                shortPress = !hold.LongFired;
            }

            if (shortPress)
                TogglePause(channel);
        }

        public void SetMute(VolumeChannel channel, bool? mute)
        {
            if (channel == null || !channel.Enabled)
                return;

            bool previous = channel.IsMuted;
            var snapshot = _audio.SetMute(channel, mute);
            OnUi(() =>
            {
                ApplySnapshot(channel, snapshot, channel.CurrentVolume, snapshot.Found || channel.IsRunning);
                if (channel.IsMuted != previous)
                    this.TriggerEvent("Channel" + channel.Slot + (channel.IsMuted ? ".Muted" : ".Unmuted"));
            });
        }

        private void SkipTrack(int slot, bool next)
        {
            var channel = GetChannel(slot);
            if (channel == null || !channel.Enabled)
                return;

            bool sent = next ? MediaControl.Next(channel) : MediaControl.Previous(channel);
            if (!sent)
                return;

            try { this.TriggerEvent("Channel" + slot + (next ? ".NextTrack" : ".PreviousTrack")); }
            catch { }
        }

        private void OnMuteHeld(int slot)
        {
            if (slot < 1 || slot >= _holds.Length)
                return;

            var hold = _holds[slot];
            lock (hold)
            {
                if (!hold.IsDown || hold.LongFired)
                    return;
                hold.LongFired = true;
            }

            var channel = GetChannel(slot);
            if (channel == null || !channel.Enabled)
                return;

            SetMute(channel, null);
        }

        private void TogglePause(VolumeChannel channel)
        {
            if (channel == null || !channel.Enabled)
                return;

            if (!MediaControl.Toggle(channel))
                return;

            int slot = channel.Slot;
            OnUi(() =>
            {
                channel.PauseHoldTick = Environment.TickCount;
                channel.IsPaused = !channel.IsPaused;
                try { this.TriggerEvent("Channel" + slot + ".PlayPause"); }
                catch { }
            });
        }

        private void RegisterSlot(int slot)
        {
            if (slot < 1 || slot > AppVolumeSettings.MaxSlots || !_registeredSlots.Add(slot))
                return;

            string prefix = "Channel" + slot;
            this.AttachDelegate(prefix + ".Volume", () => GetChannel(slot)?.CurrentVolume ?? 0);
            this.AttachDelegate(prefix + ".Volume01", () => GetChannel(slot)?.Volume01 ?? 0.0);
            this.AttachDelegate(prefix + ".Peak", () => (int)Math.Round((GetChannel(slot)?.CurrentPeak ?? 0) * 100.0));
            this.AttachDelegate(prefix + ".Peak01", () => GetChannel(slot)?.CurrentPeak ?? 0.0);
            this.AttachDelegate(prefix + ".Muted", () => (GetChannel(slot)?.IsMuted ?? false) ? 1 : 0);
            this.AttachDelegate(prefix + ".Paused", () => (GetChannel(slot)?.IsPaused ?? false) ? 1 : 0);
            this.AttachDelegate(prefix + ".LedR", () => VolumeLighting.For(GetChannel(slot)).R);
            this.AttachDelegate(prefix + ".LedG", () => VolumeLighting.For(GetChannel(slot)).G);
            this.AttachDelegate(prefix + ".LedB", () => VolumeLighting.For(GetChannel(slot)).B);
            this.AttachDelegate(prefix + ".IsRunning", () => (GetChannel(slot)?.IsRunning ?? false) ? 1 : 0);
            this.AttachDelegate(prefix + ".Enabled", () => (GetChannel(slot)?.Enabled ?? false) ? 1 : 0);
            this.AttachDelegate(prefix + ".Name", () => GetChannel(slot)?.Name ?? "");
            this.AttachDelegate(prefix + ".Step", () => GetChannel(slot)?.StepPercent ?? 5);
            this.AttachDelegate(prefix + ".TargetCount", () => GetChannel(slot)?.Targets?.Count ?? 0);

            this.AddAction(prefix + ".VolumeUp", (a, b) =>
            {
                var channel = GetChannel(slot);
                if (channel != null)
                    ChangeVolume(channel, channel.StepPercent);
            });
            this.AddAction(prefix + ".VolumeDown", (a, b) =>
            {
                var channel = GetChannel(slot);
                if (channel != null)
                    ChangeVolume(channel, -channel.StepPercent);
            });
            this.AddInputMapping(
                prefix + ".MuteToggle",
                (a, b) => MuteButtonDown(GetChannel(slot)),
                (a, b) => MuteButtonUp(GetChannel(slot)));
            this.AddInputMapping(
                prefix + ".NextTrack",
                (a, b) => SkipTrack(slot, true),
                (a, b) => { });
            this.AddInputMapping(
                prefix + ".PreviousTrack",
                (a, b) => SkipTrack(slot, false),
                (a, b) => { });

            this.AddEvent(prefix + ".VolumeChanged");
            this.AddEvent(prefix + ".PlayPause");
            this.AddEvent(prefix + ".NextTrack");
            this.AddEvent(prefix + ".PreviousTrack");
            this.AddEvent(prefix + ".Muted");
            this.AddEvent(prefix + ".Unmuted");
        }

        private void RefreshChannel(VolumeChannel channel)
        {
            if (channel == null)
                return;

            if (!channel.Enabled || channel.Targets == null || channel.Targets.Count == 0)
            {
                OnUi(() =>
                {
                    channel.IsRunning = false;
                    channel.CurrentPeak = 0;
                    channel.StatusText = !channel.Enabled ? "Выключен" : "Выберите приложение";
                    channel.TouchLighting();
                });
                return;
            }

            var snapshot = _audio.Query(channel);
            bool appOpen = snapshot.Found || _audio.IsAppOpen(channel);
            if (snapshot.Found && channel.HasPendingVolume)
                snapshot = _audio.SetVolume(channel, channel.CurrentVolume);

            OnUi(() => ApplySnapshot(channel, snapshot, channel.CurrentVolume, appOpen));
        }

        private void ApplySnapshot(VolumeChannel channel, ChannelSnapshot snapshot, int fallbackVolume, bool appOpen)
        {
            fallbackVolume = fallbackVolume < 0 ? 0 : (fallbackVolume > 100 ? 100 : fallbackVolume);

            if (snapshot != null && snapshot.Found)
            {
                channel.IsRunning = true;
                channel.CurrentVolume = snapshot.Volume;
                channel.IsMuted = snapshot.Muted;
                channel.CurrentPeak = snapshot.Peak;
                channel.HasPendingVolume = false;
                UpdatePauseFromAudio(channel);
                channel.StatusText = snapshot.Muted
                    ? snapshot.ProcessName + " · без звука"
                    : snapshot.ProcessName;
                if (channel.IsPaused)
                    channel.StatusText += " · пауза";
                channel.TouchLighting();
                return;
            }

            channel.IsRunning = appOpen;
            channel.CurrentPeak = 0;
            channel.CurrentVolume = fallbackVolume;
            channel.HasPendingVolume = true;
            channel.StatusText = appOpen ? "Приложение открыто" : "Приложение не запущено";
            if (appOpen && channel.IsMuted)
                channel.StatusText += " · без звука";
            if (appOpen && channel.IsPaused)
                channel.StatusText += " · пауза";
            channel.TouchLighting();
        }

        private static void UpdatePauseFromAudio(VolumeChannel channel)
        {
            if (!channel.IsPaused || channel.IsMuted || channel.CurrentPeak < 0.05)
                return;

            int age = unchecked(Environment.TickCount - channel.PauseHoldTick);
            if (channel.PauseHoldTick == 0 || age > 1500)
                channel.IsPaused = false;
        }

        public VolumeChannel FindChannel(int slot)
        {
            return GetChannel(slot);
        }

        private VolumeChannel GetChannel(int slot)
        {
            return Settings?.Channels?.FirstOrDefault(channel => channel.Slot == slot);
        }

        private static void OnUi(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }

        private sealed class MuteHold
        {
            public bool IsDown;
            public bool LongFired;
            public System.Threading.Timer Timer;
        }

        private static Bitmap CreateMenuIcon()
        {
            var bmp = new Bitmap(24, 24);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using (var brush = new SolidBrush(System.Drawing.Color.White))
                using (var pen = new System.Drawing.Pen(System.Drawing.Color.White, 1.6f))
                {
                    g.FillPolygon(brush, new[]
                    {
                        new Point(3, 9),
                        new Point(8, 9),
                        new Point(13, 4),
                        new Point(13, 20),
                        new Point(8, 15),
                        new Point(3, 15)
                    });
                    g.DrawArc(pen, 13, 7, 6, 10, -60, 120);
                    g.DrawArc(pen, 16, 4, 6, 16, -60, 120);
                }
            }

            return bmp;
        }
    }
}

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace User.AppVolumeController
{
    public class AppVolumeSettings
    {
        public const int MaxSlots = 16;

        public ObservableCollection<VolumeChannel> Channels { get; set; } = new ObservableCollection<VolumeChannel>();

        public void Normalize()
        {
            if (Channels == null)
                Channels = new ObservableCollection<VolumeChannel>();

            for (int i = Channels.Count - 1; i >= 0; i--)
            {
                if (Channels[i] == null)
                    Channels.RemoveAt(i);
            }

            if (Channels.Count > 0 && Channels.All(channel => channel.Slot <= 0))
            {
                for (int i = 0; i < Channels.Count && i < MaxSlots; i++)
                    Channels[i].Slot = i + 1;
            }

            var used = new HashSet<int>();
            foreach (var channel in Channels.ToList())
            {
                if (channel.Slot >= 1 && channel.Slot <= MaxSlots && used.Add(channel.Slot))
                    continue;

                int free = FirstFreeSlot(used);
                if (free == 0)
                {
                    Channels.Remove(channel);
                    continue;
                }

                channel.Slot = free;
                used.Add(free);
            }

            var kept = Channels.Where(channel => !IsUnusedDefault(channel)).ToList();
            if (kept.Count == 0)
            {
                Channels.Clear();
                Channels.Add(CreateChannel(1));
            }
            else if (kept.Count != Channels.Count)
            {
                Channels.Clear();
                foreach (var channel in kept)
                    Channels.Add(channel);
            }

            foreach (var channel in Channels)
                Prepare(channel);
        }

        public static VolumeChannel CreateChannel(int slot)
        {
            var channel = new VolumeChannel
            {
                Slot = slot,
                Name = "",
                Enabled = true,
                StepPercent = 5
            };
            channel.EnsureColors();
            channel.HookTargets();
            channel.NotifyDisplay();
            channel.TouchLighting();
            return channel;
        }

        public static int FirstFreeSlot(IEnumerable<int> used)
        {
            var taken = new HashSet<int>(used ?? Enumerable.Empty<int>());
            for (int slot = 1; slot <= MaxSlots; slot++)
            {
                if (!taken.Contains(slot))
                    return slot;
            }

            return 0;
        }

        private static bool IsUnusedDefault(VolumeChannel channel)
        {
            bool defaultName = string.IsNullOrWhiteSpace(channel.Name) ||
                               Regex.IsMatch(channel.Name.Trim(), @"^(Канал|Channel)\s+\d+$");
            bool noTargets = channel.Targets == null || channel.Targets.Count == 0;
            return !channel.Enabled && noTargets && defaultName;
        }

        private static void Prepare(VolumeChannel channel)
        {
            if (channel.Targets == null)
                channel.Targets = new ObservableCollection<VolumeTarget>();

            int step = channel.StepPercent;
            if (step < 1)
                channel.StepPercent = 1;
            else if (step > 100)
                channel.StepPercent = 100;

            channel.IsRunning = false;
            channel.CurrentPeak = 0;
            channel.HasPendingVolume = false;
            channel.IsPaused = false;
            channel.PauseHoldTick = 0;
            channel.EnsureColors();
            channel.HookTargets();
            channel.NotifyDisplay();
            channel.TouchLighting();
        }
    }

    public class VolumeChannel : INotifyPropertyChanged
    {
        private bool _enabled = true;
        private string _name = "";
        private int _stepPercent = 5;
        private int _currentVolume;
        private double _currentPeak;
        private bool _isMuted;
        private bool _isRunning;
        private string _statusText = "Выберите приложение";
        private int _slot;

        public VolumeChannel()
        {
            HookTargets();
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                OnPropertyChanged();
                NotifyDisplay();
                TouchLighting();
            }
        }

        public string Name
        {
            get => _name;
            set
            {
                var next = value ?? "";
                if (_name == next) return;
                _name = next;
                OnPropertyChanged();
                NotifyDisplay();
            }
        }

        public int StepPercent
        {
            get => _stepPercent;
            set
            {
                var next = value < 1 ? 1 : (value > 100 ? 100 : value);
                if (_stepPercent == next)
                {
                    OnPropertyChanged(nameof(StepText));
                    return;
                }
                _stepPercent = next;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StepText));
            }
        }

        public string StepText
        {
            get => _stepPercent.ToString();
            set
            {
                if (int.TryParse((value ?? "").Trim(), out int next))
                    StepPercent = next;
                else
                    OnPropertyChanged();
            }
        }

        public ObservableCollection<VolumeTarget> Targets { get; set; } = new ObservableCollection<VolumeTarget>();

        public int CurrentVolume
        {
            get => _currentVolume;
            set
            {
                var next = value < 0 ? 0 : (value > 100 ? 100 : value);
                if (_currentVolume == next) return;
                _currentVolume = next;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Volume01));
                TouchLighting();
            }
        }

        public double CurrentPeak
        {
            get => _currentPeak;
            set
            {
                if (System.Math.Abs(_currentPeak - value) < 0.001) return;
                _currentPeak = value;
                OnPropertyChanged();
            }
        }

        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                if (_isMuted == value) return;
                _isMuted = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MuteButtonText));
                TouchLighting();
            }
        }

        private bool _isPaused;

        public bool IsPaused
        {
            get => _isPaused;
            set
            {
                if (_isPaused == value) return;
                _isPaused = value;
                OnPropertyChanged();
                TouchLighting();
            }
        }

        public int PauseHoldTick { get; set; }

        public bool ColorsInitialized { get; set; }

        private bool _showInactiveColor;

        public bool ShowInactiveColor
        {
            get => _showInactiveColor;
            set
            {
                if (_showInactiveColor == value) return;
                _showInactiveColor = value;
                OnPropertyChanged();
                TouchLighting();
            }
        }

        public bool InactiveColorReady { get; set; }

        public int ColorR { get; set; }
        public int ColorG { get; set; }
        public int ColorB { get; set; }
        public int MuteR { get; set; }
        public int MuteG { get; set; }
        public int MuteB { get; set; }
        public int PauseR { get; set; }
        public int PauseG { get; set; }
        public int PauseB { get; set; }
        public int InactiveR { get; set; }
        public int InactiveG { get; set; }
        public int InactiveB { get; set; }

        public string VolumeSwatch => ColorR + "," + ColorG + "," + ColorB;
        public string MuteSwatch => MuteR + "," + MuteG + "," + MuteB;
        public string PauseSwatch => PauseR + "," + PauseG + "," + PauseB;
        public string InactiveSwatch => InactiveR + "," + InactiveG + "," + InactiveB;

        private string _liveSwatch = "0,0,0";
        private string _lightingStateText = "";

        public string LiveSwatch => _liveSwatch;
        public string LightingStateText => _lightingStateText;

        public void EnsureColors()
        {
            bool changed = false;
            if (!ColorsInitialized)
            {
                ColorR = 0;
                ColorG = 170;
                ColorB = 255;
                MuteR = 255;
                MuteG = 40;
                MuteB = 40;
                PauseR = 255;
                PauseG = 160;
                PauseB = 0;
                ColorsInitialized = true;
                changed = true;
            }

            if (!InactiveColorReady)
            {
                InactiveR = 120;
                InactiveG = 120;
                InactiveB = 120;
                InactiveColorReady = true;
                changed = true;
            }

            if (changed)
                NotifyColors();
        }

        public void NotifyColors()
        {
            OnPropertyChanged(nameof(VolumeSwatch));
            OnPropertyChanged(nameof(MuteSwatch));
            OnPropertyChanged(nameof(PauseSwatch));
            OnPropertyChanged(nameof(InactiveSwatch));
            TouchLighting();
        }

        public bool LeavesWheelLighting => !VolumeLighting.For(this).Paint;

        public void TouchLighting()
        {
            var light = VolumeLighting.For(this);
            var swatch = light.Paint ? light.R + "," + light.G + "," + light.B : "0,0,0";
            if (_liveSwatch != swatch)
            {
                _liveSwatch = swatch;
                OnPropertyChanged(nameof(LiveSwatch));
            }

            OnPropertyChanged(nameof(LeavesWheelLighting));

            string state;
            if (!Enabled)
                state = "Канал выключен";
            else if (!IsRunning && ShowInactiveColor)
                state = "Приложение закрыто";
            else if (!IsRunning)
                state = "Подсветка штурвала";
            else if (IsPaused && IsMuted)
                state = "Пауза и без звука";
            else if (IsPaused)
                state = "Пауза";
            else if (IsMuted)
                state = "Без звука";
            else
                state = "Яркость " + CurrentVolume + "%";

            if (_lightingStateText != state)
            {
                _lightingStateText = state;
                OnPropertyChanged(nameof(LightingStateText));
            }
        }

        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (_isRunning == value) return;
                _isRunning = value;
                OnPropertyChanged();
                TouchLighting();
            }
        }

        private bool _detailsCollapsed;

        public bool DetailsCollapsed
        {
            get => _detailsCollapsed;
            set
            {
                if (_detailsCollapsed == value) return;
                _detailsCollapsed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CollapseLabel));
            }
        }

        public string CollapseLabel => _detailsCollapsed ? "Развернуть" : "Свернуть";

        public string StatusText
        {
            get => _statusText;
            set
            {
                var next = value ?? "";
                if (_statusText == next) return;
                _statusText = next;
                OnPropertyChanged();
            }
        }

        public int Slot
        {
            get => _slot;
            set
            {
                if (_slot == value) return;
                _slot = value;
                OnPropertyChanged();
                NotifyDisplay();
            }
        }

        public bool HasPendingVolume { get; set; }

        public bool HasTargets => Targets != null && Targets.Count > 0;

        public double Volume01 => CurrentVolume / 100.0;

        public string DisplayTitle => string.IsNullOrWhiteSpace(Name) ? "Канал " + Slot : Name;

        public string StepGroup => "step-" + Slot;

        public string MuteButtonText => IsMuted ? "Включить" : "Без звука";

        public string PropertyHint => "Для подсветки: AppVolumePlugin.Channel" + Slot + ".Volume";

        public string VolumeUpActionName => "AppVolumePlugin.Channel" + Slot + ".VolumeUp";

        public string VolumeDownActionName => "AppVolumePlugin.Channel" + Slot + ".VolumeDown";

        public string MuteToggleActionName => "AppVolumePlugin.Channel" + Slot + ".MuteToggle";

        public string NextTrackActionName => "AppVolumePlugin.Channel" + Slot + ".NextTrack";

        public string PreviousTrackActionName => "AppVolumePlugin.Channel" + Slot + ".PreviousTrack";

        public string LouderFriendlyName => DisplayTitle + " — громче";

        public string QuieterFriendlyName => DisplayTitle + " — тише";

        public string MuteFriendlyName => DisplayTitle + " — пауза / без звука";

        public string NextTrackFriendlyName => DisplayTitle + " — следующий трек";

        public string PreviousTrackFriendlyName => DisplayTitle + " — предыдущий трек";

        public void HookTargets()
        {
            if (Targets == null)
                Targets = new ObservableCollection<VolumeTarget>();

            Targets.CollectionChanged -= OnTargetsChanged;
            Targets.CollectionChanged += OnTargetsChanged;
            OnPropertyChanged(nameof(HasTargets));
        }

        public void NotifyDisplay()
        {
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(StepGroup));
            OnPropertyChanged(nameof(PropertyHint));
            OnPropertyChanged(nameof(VolumeUpActionName));
            OnPropertyChanged(nameof(VolumeDownActionName));
            OnPropertyChanged(nameof(MuteToggleActionName));
            OnPropertyChanged(nameof(NextTrackActionName));
            OnPropertyChanged(nameof(PreviousTrackActionName));
            OnPropertyChanged(nameof(LouderFriendlyName));
            OnPropertyChanged(nameof(QuieterFriendlyName));
            OnPropertyChanged(nameof(MuteFriendlyName));
            OnPropertyChanged(nameof(NextTrackFriendlyName));
            OnPropertyChanged(nameof(PreviousTrackFriendlyName));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private void OnTargetsChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasTargets));
        }
    }

    public class VolumeTarget : INotifyPropertyChanged
    {
        private string _processName = "";
        private string _windowTitleFilter = "";
        private string _displayName = "";

        public string ProcessName
        {
            get => _processName;
            set
            {
                var next = value ?? "";
                if (_processName == next) return;
                _processName = next;
                OnPropertyChanged();
                NotifyText();
            }
        }

        public string WindowTitleFilter
        {
            get => _windowTitleFilter;
            set
            {
                var next = value ?? "";
                if (_windowTitleFilter == next) return;
                _windowTitleFilter = next;
                OnPropertyChanged();
                NotifyText();
            }
        }

        public string DisplayName
        {
            get => _displayName;
            set
            {
                var next = value ?? "";
                if (_displayName == next) return;
                _displayName = next;
                OnPropertyChanged();
                NotifyText();
            }
        }

        public string TitleLine
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(DisplayName) &&
                    !string.Equals(DisplayName, ProcessName, System.StringComparison.OrdinalIgnoreCase))
                    return DisplayName;
                return string.IsNullOrWhiteSpace(ProcessName) ? "Приложение" : ProcessName;
            }
        }

        public string DetailLine
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(WindowTitleFilter))
                    return "Только окно «" + WindowTitleFilter + "»";
                return string.IsNullOrWhiteSpace(ProcessName) ? "Вся программа" : "Вся программа · " + ProcessName;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void NotifyText()
        {
            OnPropertyChanged(nameof(TitleLine));
            OnPropertyChanged(nameof(DetailLine));
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}

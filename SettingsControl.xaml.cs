using System.Windows;
using System.Windows.Controls;

namespace User.AppVolumeController
{
    public partial class SettingsControl : UserControl
    {
        public AppVolumePlugin Plugin { get; }

        public SettingsControl()
        {
            InitializeComponent();
        }

        public SettingsControl(AppVolumePlugin plugin) : this()
        {
            Plugin = plugin;
            DataContext = plugin;
            if (Plugin?.Settings?.Channels != null)
                Plugin.Settings.Channels.CollectionChanged += ChannelsChanged;
            Unloaded += (_, __) =>
            {
                if (Plugin?.Settings?.Channels != null)
                    Plugin.Settings.Channels.CollectionChanged -= ChannelsChanged;
            };
            UpdateChrome();
        }

        private void ChannelsChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            UpdateChrome();
        }

        private void UpdateChrome()
        {
            int count = Plugin?.Settings?.Channels?.Count ?? 0;
            bool canAdd = count < AppVolumeSettings.MaxSlots;
            EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AddButton.IsEnabled = canAdd;
            AddButton.Content = canAdd ? "Добавить канал" : "Уже 16 каналов";
        }

        private VolumeChannel ChannelOf(object sender)
        {
            return FindChannel(sender as FrameworkElement);
        }

        private static VolumeChannel FindChannel(FrameworkElement element)
        {
            while (element != null)
            {
                if (element.DataContext is VolumeChannel channel)
                    return channel;
                element = System.Windows.Media.VisualTreeHelper.GetParent(element) as FrameworkElement;
            }

            return null;
        }

        private void Persist_Click(object sender, RoutedEventArgs e)
        {
            Plugin?.SaveSettings();
        }

        private void ToggleDetails_Click(object sender, RoutedEventArgs e)
        {
            var channel = ChannelOf(sender);
            if (channel == null)
                return;

            channel.DetailsCollapsed = !channel.DetailsCollapsed;
            Plugin?.SaveSettings();
        }

        private void AddChannel_Click(object sender, RoutedEventArgs e)
        {
            Plugin?.AddChannel();
            UpdateChrome();
        }

        private void RemoveChannel_Click(object sender, RoutedEventArgs e)
        {
            var channel = ChannelOf(sender);
            if (channel == null)
                return;

            Plugin?.RemoveChannel(channel);
            UpdateChrome();
        }

        private void VolumeUp_Click(object sender, RoutedEventArgs e)
        {
            var channel = ChannelOf(sender);
            if (channel == null)
                return;
            Plugin?.ChangeVolume(channel, channel.StepPercent);
        }

        private void VolumeDown_Click(object sender, RoutedEventArgs e)
        {
            var channel = ChannelOf(sender);
            if (channel == null)
                return;
            Plugin?.ChangeVolume(channel, -channel.StepPercent);
        }

        private void PickColor_Click(object sender, RoutedEventArgs e)
        {
            var channel = ChannelOf(sender);
            var kind = (sender as FrameworkElement)?.Tag as string;
            if (channel == null || kind == null)
                return;

            System.Drawing.Color current;
            if (kind == "mute")
                current = System.Drawing.Color.FromArgb(channel.MuteR, channel.MuteG, channel.MuteB);
            else if (kind == "pause")
                current = System.Drawing.Color.FromArgb(channel.PauseR, channel.PauseG, channel.PauseB);
            else if (kind == "inactive")
                current = System.Drawing.Color.FromArgb(channel.InactiveR, channel.InactiveG, channel.InactiveB);
            else
                current = System.Drawing.Color.FromArgb(channel.ColorR, channel.ColorG, channel.ColorB);

            using (var dialog = new System.Windows.Forms.ColorDialog())
            {
                dialog.FullOpen = true;
                dialog.Color = current;
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return;

                if (kind == "mute")
                {
                    channel.MuteR = dialog.Color.R;
                    channel.MuteG = dialog.Color.G;
                    channel.MuteB = dialog.Color.B;
                }
                else if (kind == "pause")
                {
                    channel.PauseR = dialog.Color.R;
                    channel.PauseG = dialog.Color.G;
                    channel.PauseB = dialog.Color.B;
                }
                else if (kind == "inactive")
                {
                    channel.InactiveR = dialog.Color.R;
                    channel.InactiveG = dialog.Color.G;
                    channel.InactiveB = dialog.Color.B;
                    channel.InactiveColorReady = true;
                }
                else
                {
                    channel.ColorR = dialog.Color.R;
                    channel.ColorG = dialog.Color.G;
                    channel.ColorB = dialog.Color.B;
                }
            }

            channel.ColorsInitialized = true;
            channel.NotifyColors();
            Plugin?.SaveSettings();
        }

        private void Mute_Click(object sender, RoutedEventArgs e)
        {
            Plugin?.SetMute(ChannelOf(sender), null);
        }

        private async void AddRunning_Click(object sender, RoutedEventArgs e)
        {
            var channel = ChannelOf(sender);
            if (channel == null)
                return;

            var dialog = new AppPickerDialog();
            var result = await dialog.ShowDialogWindowAsync(this);
            if (result != System.Windows.Forms.DialogResult.OK || dialog.SelectedTarget == null)
                return;

            AddTarget(channel, dialog.SelectedTarget);
        }

        private async void AddManual_Click(object sender, RoutedEventArgs e)
        {
            var channel = ChannelOf(sender);
            if (channel == null)
                return;

            var dialog = new ManualTargetDialog();
            var result = await dialog.ShowDialogWindowAsync(this);
            if (result != System.Windows.Forms.DialogResult.OK || dialog.SelectedTarget == null)
                return;

            AddTarget(channel, dialog.SelectedTarget);
        }

        private void AddTarget(VolumeChannel channel, VolumeTarget target)
        {
            channel.Enabled = true;
            channel.Targets.Add(target);
            if (string.IsNullOrWhiteSpace(channel.Name))
                channel.Name = string.IsNullOrWhiteSpace(target.ProcessName) ? target.DisplayName : target.ProcessName;
            Plugin?.RefreshAllChannels();
            Plugin?.SaveSettings();
        }

        private void RemoveTarget_Click(object sender, RoutedEventArgs e)
        {
            var target = (sender as FrameworkElement)?.DataContext as VolumeTarget;
            var channel = FindChannel(sender as FrameworkElement);
            if (channel?.Targets == null || target == null)
                return;

            channel.Targets.Remove(target);
            Plugin?.RefreshAllChannels();
            Plugin?.SaveSettings();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SimHub.Plugins.UI;

namespace User.AppVolumeController
{
    public partial class AppPickerDialog : SHDialogContentBase
    {
        private readonly AudioSessionService _audio = new AudioSessionService();
        private List<AppCandidate> _audioApps = new List<AppCandidate>();
        private List<AppCandidate> _windows = new List<AppCandidate>();

        private AppCandidate _picked;

        public VolumeTarget SelectedTarget { get; private set; }

        public AppPickerDialog()
        {
            InitializeComponent();
            ShowOk = true;
            ShowCancel = true;
            Title = "Выбор приложения или окна";
            Loaded += AppPickerDialog_Loaded;
        }

        private void AppPickerDialog_Loaded(object sender, RoutedEventArgs e)
        {
            Reload();
        }

        public override bool Ok()
        {
            var candidate = _picked ?? AudioList.SelectedItem as AppCandidate ?? WindowList.SelectedItem as AppCandidate;
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.ProcessName))
            {
                SelectedTarget = null;
                return false;
            }

            bool bindTitle = BindByTitleBox.IsChecked == true && !string.IsNullOrWhiteSpace(candidate.WindowTitle);
            SelectedTarget = new VolumeTarget
            {
                ProcessName = candidate.ProcessName,
                WindowTitleFilter = bindTitle ? candidate.WindowTitle : "",
                DisplayName = string.IsNullOrWhiteSpace(candidate.WindowTitle) ? candidate.ProcessName : candidate.WindowTitle
            };
            return true;
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            Reload();
        }

        private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ListBox list && list.SelectedItem is AppCandidate candidate)
                _picked = candidate;
        }

        private void List_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!((sender as ListBox)?.SelectedItem is AppCandidate candidate))
                return;

            _picked = candidate;
            if (Ok())
                Close(System.Windows.Forms.DialogResult.OK);
        }

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void Reload()
        {
            try
            {
                _audioApps = _audio.ListAudioApps();
            }
            catch
            {
                _audioApps = new List<AppCandidate>();
            }

            try
            {
                _windows = _audio.ListWindows();
            }
            catch
            {
                _windows = new List<AppCandidate>();
            }

            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var filter = FilterBox?.Text?.Trim() ?? "";
            AudioList.ItemsSource = Filter(_audioApps, filter);
            WindowList.ItemsSource = Filter(_windows, filter);
        }

        private static List<AppCandidate> Filter(IEnumerable<AppCandidate> source, string filter)
        {
            if (source == null)
                return new List<AppCandidate>();
            if (string.IsNullOrWhiteSpace(filter))
                return source.ToList();

            return source.Where(item =>
                    (item.ProcessName ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (item.WindowTitle ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
        }
    }
}

using SimHub.Plugins.UI;

namespace User.AppVolumeController
{
    public partial class ManualTargetDialog : SHDialogContentBase
    {
        public VolumeTarget SelectedTarget { get; private set; }

        public ManualTargetDialog()
        {
            InitializeComponent();
            ShowOk = true;
            ShowCancel = true;
            Title = "Добавить приложение";
        }

        public override bool Ok()
        {
            var process = (ProcessBox.Text ?? "").Trim();
            var title = (TitleBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(process) && string.IsNullOrWhiteSpace(title))
                return false;

            SelectedTarget = new VolumeTarget
            {
                ProcessName = process,
                WindowTitleFilter = title,
                DisplayName = string.IsNullOrWhiteSpace(title) ? process : title
            };
            return true;
        }
    }
}

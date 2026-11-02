using System.Collections.Generic;
using System.Windows.Controls;
using SimHub.Plugins;
using SimHub.Plugins.DataPlugins.RGBDriver;
using SimHub.Plugins.DataPlugins.RGBDriver.LedsContainers.Attributes;
using SimHub.Plugins.DataPlugins.RGBDriver.LedsContainers.Base;
using SimHub.Plugins.DataPlugins.RGBDriver.Settings;
using SimHub.Plugins.DataPlugins.RGBDriverCommon;

namespace User.AppVolumeController
{
    [ContainerMetadata(82, "Громкость канала", "Яркость от громкости 0–100. Отдельный цвет для без звука и паузы.", "Effects")]
    public class VolumeLedEffect : LedsContainerBase, ILed_LedCount
    {
        private int _channelNumber = 1;
        private int _ledCount = 1;

        public int ChannelNumber
        {
            get => _channelNumber;
            set
            {
                int next = value < 1 ? 1 : (value > AppVolumeSettings.MaxSlots ? AppVolumeSettings.MaxSlots : value);
                _channelNumber = next;
            }
        }

        public int LedCount
        {
            get => _ledCount;
            set => _ledCount = value < 1 ? 1 : (value > 1024 ? 1024 : value);
        }

        public override bool IsAllowedOutOfGame => true;

        public override void LoadDefaultSettings()
        {
            if (ChannelNumber < 1)
                ChannelNumber = 1;
            if (LedCount < 1)
                LedCount = 1;
        }

        public override LedResult SetResultBase(LedsGameData data, LedResult result, List<Profile> includedFrom)
        {
            int count = LedCount < 1 ? 1 : LedCount;
            result.Clear(count);
            var light = VolumeLighting.For(FindChannel());
            var color = light.Paint ? light.Color : VolumeLighting.Clear;
            for (int i = 0; i < count; i++)
                result[i] = color;
            return result;
        }

        protected override bool IsActive(LedsGameData data)
        {
            return true;
        }

        protected override UserControl GetEditControl()
        {
            return new VolumeLedEffectControl { DataContext = this };
        }

        private static VolumeChannel FindChannel(int slot)
        {
            var plugin = PluginManager.GetInstance()?.GetPlugin<AppVolumePlugin>();
            return plugin?.FindChannel(slot);
        }

        private VolumeChannel FindChannel()
        {
            return FindChannel(ChannelNumber);
        }
    }
}

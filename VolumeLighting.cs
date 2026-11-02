using System;
using System.Drawing;

namespace User.AppVolumeController
{
    internal static class VolumeLighting
    {
        public static Color For(VolumeChannel channel)
        {
            if (channel == null || !channel.Enabled || !channel.IsRunning)
                return Color.Black;

            if (channel.IsPaused && channel.IsMuted)
            {
                bool showPause = ((Environment.TickCount / 600) & 1) == 0;
                return showPause
                    ? Rgb(channel.PauseR, channel.PauseG, channel.PauseB)
                    : Rgb(channel.MuteR, channel.MuteG, channel.MuteB);
            }

            if (channel.IsPaused)
                return Rgb(channel.PauseR, channel.PauseG, channel.PauseB);
            if (channel.IsMuted)
                return Rgb(channel.MuteR, channel.MuteG, channel.MuteB);

            double amount = channel.CurrentVolume / 100.0;
            if (amount <= 0)
                return Color.Black;

            return ScaleColor(channel.ColorR, channel.ColorG, channel.ColorB, amount);
        }

        private static Color ScaleColor(int red, int green, int blue, double amount)
        {
            int max = Math.Max(red, Math.Max(green, blue));
            if (max <= 0)
                return Color.Black;

            // На кнопках канал темнее ~20% шкалы не загорается, поэтому
            // любой уровень выше нуля сразу начинается выше этого порога.
            const int visibleFloor = 58;
            int peak = max <= visibleFloor
                ? (int)Math.Round(max * amount)
                : visibleFloor + (int)Math.Round((max - visibleFloor) * amount);
            if (peak < 1)
                peak = 1;
            if (peak > max)
                peak = max;

            double factor = (double)peak / max;
            return Rgb(Scale(red, factor), Scale(green, factor), Scale(blue, factor));
        }

        private static int Scale(int component, double scale)
        {
            return Clamp((int)Math.Round(component * scale));
        }

        private static Color Rgb(int red, int green, int blue)
        {
            return Color.FromArgb(Clamp(red), Clamp(green), Clamp(blue));
        }

        private static int Clamp(int value)
        {
            if (value < 0) return 0;
            if (value > 255) return 255;
            return value;
        }
    }
}

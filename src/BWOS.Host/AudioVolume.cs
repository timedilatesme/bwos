using System;
using System.Runtime.InteropServices;

namespace Bwos
{
    // Windows SDK mmdeviceapi.h / endpointvolume.h. Applies only explicit volume intents.
    internal static class AudioVolume
    {
        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IEnum
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, uint states, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
        }
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IDevice
        {
            [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        }
        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr callback);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr callback);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float level, IntPtr context);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr context);
            [PreserveSig] int GetMasterVolumeLevel(out float level);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, IntPtr context);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr context);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr context);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }
        private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
        private static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
        public static string Apply(string action, string value)
        {
            var enumerator = (IEnum)AudioCom.Enumerator(); IDevice device = null; object instance = null;
            try
            {
                Check(enumerator.GetDefaultAudioEndpoint(0, 1, out device));
                var iid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
                Check(device.Activate(ref iid, 23, IntPtr.Zero, out instance)); var volume = (IVolume)instance;
                float current; bool muted; Check(volume.GetMasterVolumeLevelScalar(out current)); Check(volume.GetMute(out muted));
                if (action == "read") return Math.Round(current * 100) + "%" + (muted ? " muted" : "");
                if (action == "mute" || action == "unmute") { Check(volume.SetMute(action == "mute", IntPtr.Zero)); return action == "mute" ? "Muted." : "Sound on."; }
                int number;
                if (action == "volumeSet" || action == "volumeChange")
                {
                    if (!int.TryParse(value, out number) || (action == "volumeSet" ? number < 0 || number > 100 : number < -100 || number > 100)) throw new ArgumentException("Volume must be between zero and one hundred");
                }
                else if (action == "volup" || action == "voldown") number = action == "volup" ? 5 : -5;
                else throw new ArgumentException("Unknown volume action");
                float next = action == "volumeSet" ? number / 100f : current + number / 100f;
                next = Math.Max(0, Math.Min(1, next)); Check(volume.SetMasterVolumeLevelScalar(next, IntPtr.Zero));
                if (next > 0 && (number > 0 || action == "volumeSet")) Check(volume.SetMute(false, IntPtr.Zero));
                return "Volume " + Math.Round(next * 100) + " percent.";
            }
            finally { Release(instance); Release(device); Release(enumerator); }
        }
    }
}

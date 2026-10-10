using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Bwos
{
    /// <summary>WinMM PCM capture. Buffers are owned by the recognition worker, never a callback.</summary>
    internal sealed class Microphone : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, Pack = 2)] private struct Format
        {
            public ushort Tag, Channels;
            public uint Rate, BytesPerSecond;
            public ushort Align, Bits, Extra;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Header
        {
            public IntPtr Data;
            public uint Length, Recorded;
            public UIntPtr User;
            public uint Flags, Loops;
            public IntPtr Next;
            public UIntPtr Reserved;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Caps
        {
            public ushort Manufacturer, Product;
            public uint Version;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
            public uint Formats;
            public ushort Channels, Reserved;
        }
        [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint waveInGetDevCaps(UIntPtr device, out Caps caps, uint size);
        [DllImport("winmm.dll")] private static extern uint waveInOpen(out IntPtr handle, uint device, ref Format format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] private static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveInStart(IntPtr handle);
        [DllImport("winmm.dll")] private static extern uint waveInReset(IntPtr handle);
        [DllImport("winmm.dll")] private static extern uint waveInClose(IntPtr handle);
        private IntPtr handle;
        private EchoCapture echo;
        private readonly int device;
        private DateTime echoLastBuffer;
        public string Processing { get; private set; }
        private int nextBuffer;
        private readonly List<IntPtr> buffers = new List<IntPtr>();
        private readonly uint headerSize = (uint)Marshal.SizeOf(typeof(Header));
        public static List<Dictionary<string, object>> Devices()
        {
            var list = new List<Dictionary<string, object>>();
            for (uint i = 0; i < waveInGetNumDevs(); i++)
            {
                Caps caps;
                if (waveInGetDevCaps(new UIntPtr(i), out caps, (uint)Marshal.SizeOf(typeof(Caps))) == 0)
                    list.Add(new Dictionary<string, object> { { "id", (int)i }, { "name", caps.Name } });
            }
            return list;
        }
        public Microphone(int device)
        {
            this.device = device;
            try { OpenEcho(); }
            catch (Exception ex) { Fallback(ex); }
        }
        private void OpenEcho()
        {
            echo = new EchoCapture(device); echoLastBuffer = DateTime.UtcNow;
            Processing = "Windows echo cancellation · noise suppression";
            Log.Write("voice input: " + Processing);
        }
        private void Fallback(Exception reason)
        {
            if (echo != null) { echo.Dispose(); echo = null; }
            Processing = "Basic microphone · echo cancellation unavailable: " + reason.Message;
            Log.Write("voice input: " + Processing);
            OpenRaw();
        }
        private void OpenRaw()
        {
            try
            {
                var format = new Format { Tag = 1, Channels = 1, Rate = 16000, BytesPerSecond = 32000, Align = 2, Bits = 16 };
                Check(waveInOpen(out handle, device < 0 ? uint.MaxValue : (uint)device, ref format, IntPtr.Zero, IntPtr.Zero, 0));
                for (int i = 0; i < 8; i++)
                {
                    IntPtr pointer = Marshal.AllocHGlobal((int)headerSize);
                    var header = new Header { Data = Marshal.AllocHGlobal(3200), Length = 3200 };
                    Marshal.StructureToPtr(header, pointer, false);
                    buffers.Add(pointer);
                    Check(waveInPrepareHeader(handle, pointer, headerSize));
                    Check(waveInAddBuffer(handle, pointer, headerSize));
                }
                Check(waveInStart(handle));
            }
            catch { Dispose(); throw; }
        }
        public void Read(Action<byte[]> receive)
        {
            if (echo != null)
            {
                var samples = new List<byte[]>();
                try
                {
                    if (echo.DevicesChanged()) { echo.Dispose(); echo = null; OpenEcho(); }
                    if (echo.Read(samples.Add)) echoLastBuffer = DateTime.UtcNow;
                    else if ((DateTime.UtcNow - echoLastBuffer).TotalSeconds > 4) throw new InvalidOperationException("Windows speaker reference stopped producing audio");
                }
                catch (Exception ex) { Fallback(ex); }
                foreach (var chunk in samples) receive(chunk);
                if (echo != null) return;
            }
            // Process in submission order even when inference temporarily falls behind capture.
            for (int i = 0; i < buffers.Count; i++)
            {
                IntPtr pointer = buffers[nextBuffer];
                var header = (Header)Marshal.PtrToStructure(pointer, typeof(Header));
                if ((header.Flags & 1) == 0) break;
                if (header.Recorded > 0)
                {
                    byte[] samples = new byte[header.Recorded];
                    Marshal.Copy(header.Data, samples, 0, samples.Length);
                    receive(samples);
                }
                Check(waveInAddBuffer(handle, pointer, headerSize));
                nextBuffer = (nextBuffer + 1) % buffers.Count;
            }
        }
        // RMS for 16-bit little-endian PCM, scaled for a readable speech-input meter.
        internal static int Level(byte[] samples)
        {
            int count = samples.Length / 2;
            if (count == 0) return 0;
            double sum = 0;
            for (int i = 0; i + 1 < samples.Length; i += 2)
            {
                double sample = (short)(samples[i] | samples[i + 1] << 8) / 32768.0;
                sum += sample * sample;
            }
            return Math.Min(100, (int)Math.Round(Math.Sqrt(sum / count) * 500));
        }
        private static void Check(uint result)
        {
            if (result != 0) throw new InvalidOperationException("Couldn't access the microphone (audio error " + result + "). Check Windows microphone permissions and the selected input.");
        }
        public void Dispose()
        {
            if (echo != null) { echo.Dispose(); echo = null; }
            if (handle != IntPtr.Zero) waveInReset(handle);
            foreach (IntPtr pointer in buffers)
            {
                var header = (Header)Marshal.PtrToStructure(pointer, typeof(Header));
                if (handle != IntPtr.Zero) waveInUnprepareHeader(handle, pointer, headerSize);
                Marshal.FreeHGlobal(header.Data);
                Marshal.FreeHGlobal(pointer);
            }
            buffers.Clear();
            if (handle != IntPtr.Zero) { waveInClose(handle); handle = IntPtr.Zero; }
        }
    }
}

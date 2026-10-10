using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Bwos
{
    // Windows Voice Capture DSP source mode synchronizes microphone capture with
    // the default speaker reference. PCM stays local; no audio is saved or uploaded.
    internal sealed class EchoCapture : IDisposable
    {
        [ComImport, Guid("745057C7-F353-4F2D-A7EE-58434477730E")] private class Aec { }
        [StructLayout(LayoutKind.Sequential)] private struct MediaType
        {
            public Guid Major, Subtype;
            public int Fixed, Temporal;
            public uint SampleSize;
            public Guid FormatType;
            public IntPtr Unknown;
            public uint FormatLength;
            public IntPtr Format;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 2)] private struct WaveFormat
        {
            public ushort Tag, Channels;
            public uint Rate, BytesPerSecond;
            public ushort Align, Bits, Extra;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Output
        {
            public IntPtr Buffer;
            public uint Status;
            public long Timestamp, Length;
        }
        [ComImport, Guid("D8AD0F58-5494-4102-97C5-EC798E59BCF4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IDmo
        {
            [PreserveSig] int GetStreamCount(out uint input, out uint output);
            [PreserveSig] int GetInputStreamInfo(uint stream, out uint flags);
            [PreserveSig] int GetOutputStreamInfo(uint stream, out uint flags);
            [PreserveSig] int GetInputType(uint stream, uint index, IntPtr type);
            [PreserveSig] int GetOutputType(uint stream, uint index, IntPtr type);
            [PreserveSig] int SetInputType(uint stream, IntPtr type, uint flags);
            [PreserveSig] int SetOutputType(uint stream, ref MediaType type, uint flags);
            [PreserveSig] int GetInputCurrentType(uint stream, IntPtr type);
            [PreserveSig] int GetOutputCurrentType(uint stream, IntPtr type);
            [PreserveSig] int GetInputSizeInfo(uint stream, out uint size, out uint lookahead, out uint alignment);
            [PreserveSig] int GetOutputSizeInfo(uint stream, out uint size, out uint alignment);
            [PreserveSig] int GetInputMaxLatency(uint stream, out long latency);
            [PreserveSig] int SetInputMaxLatency(uint stream, long latency);
            [PreserveSig] int Flush();
            [PreserveSig] int Discontinuity(uint stream);
            [PreserveSig] int AllocateStreamingResources();
            [PreserveSig] int FreeStreamingResources();
            [PreserveSig] int GetInputStatus(uint stream, out uint flags);
            [PreserveSig] int ProcessInput(uint stream, IntPtr buffer, uint flags, long timestamp, long length);
            [PreserveSig] int ProcessOutput(uint flags, uint count, [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] Output[] output, out uint status);
            [PreserveSig] int Lock(int locked);
        }
        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IEnum
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, uint states, out ICollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDevice device);
        }
        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface ICollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IDevice device);
        }
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IDevice
        {
            [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            [PreserveSig] int OpenPropertyStore(uint mode, out IStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out uint state);
        }
        [StructLayout(LayoutKind.Sequential)] private struct Key { public Guid Format; public uint Id; }
        [StructLayout(LayoutKind.Explicit, Size = 24)] private struct Variant
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public int Int;
            [FieldOffset(8)] public short Bool;
            [FieldOffset(8)] public IntPtr Pointer;
        }
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out Key key);
            [PreserveSig] int GetValue(ref Key key, out Variant value);
            [PreserveSig] int SetValue(ref Key key, ref Variant value);
            [PreserveSig] int Commit();
        }
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref Variant value);
        private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
        private static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
        private object instance;
        private IDmo dmo;
        private CaptureBuffer buffer;
        private IntPtr bufferPointer;
        private SilentOutput output;
        private string renderId, captureId;
        private readonly int device;
        private DateTime checkDevices;

        public EchoCapture(int selectedDevice)
        {
            device = selectedDevice;
            try
            {
                int render = Endpoint(0, null, out renderId);
                int capture = Endpoint(1, SelectedName(device), out captureId);
                // Source mode needs an active render stream even in a quiet room.
                // A silent shared WinMM stream keeps its speaker reference running.
                output = new SilentOutput();
                instance = new Aec(); dmo = (IDmo)instance;
                var store = (IStore)instance;
                Set(store, 2, new Variant { Type = 3, Int = 0 }); // AEC, no microphone array processing
                Set(store, 3, new Variant { Type = 11, Bool = -1 }); // source mode
                Set(store, 4, new Variant { Type = 3, Int = (render << 16) | capture });
                // Noise suppression is enabled by the system's default feature settings.
                var format = new WaveFormat { Tag = 1, Channels = 1, Rate = 16000, BytesPerSecond = 32000, Align = 2, Bits = 16 };
                var type = new MediaType { Major = new Guid("73647561-0000-0010-8000-00AA00389B71"), Subtype = new Guid("00000001-0000-0010-8000-00AA00389B71"), Fixed = 1,
                    FormatType = new Guid("05589F81-C356-11CE-BF01-00AA0055595A"), FormatLength = (uint)Marshal.SizeOf(typeof(WaveFormat)) };
                type.Format = Marshal.AllocHGlobal((int)type.FormatLength);
                try { Marshal.StructureToPtr(format, type.Format, false); Check(dmo.SetOutputType(0, ref type, 0)); }
                finally { Marshal.FreeHGlobal(type.Format); }
                buffer = new CaptureBuffer(3200);
                bufferPointer = Marshal.GetComInterfaceForObject(buffer, typeof(ICaptureBuffer));
                Check(dmo.AllocateStreamingResources());
                checkDevices = DateTime.UtcNow;
            }
            catch { Dispose(); throw; }
        }
        private static void Set(IStore store, uint id, Variant value)
        {
            var key = new Key { Format = new Guid("6F52C567-0360-4BD2-9617-CCBF1421C939"), Id = id };
            Check(store.SetValue(ref key, ref value));
        }
        private static string SelectedName(int device)
        {
            if (device < 0) return null;
            foreach (var entry in Microphone.Devices()) if ((int)entry["id"] == device) return (string)entry["name"];
            throw new InvalidOperationException("Selected microphone is no longer available");
        }
        private static int Endpoint(int flow, string name, out string selectedId)
        {
            var enumerator = (IEnum)AudioCom.Enumerator(); ICollection collection = null; IDevice defaultDevice = null;
            selectedId = null;
            try
            {
                string defaultId = null;
                if (name == null) { Check(enumerator.GetDefaultAudioEndpoint(flow, 1, out defaultDevice)); Check(defaultDevice.GetId(out defaultId)); }
                Check(enumerator.EnumAudioEndpoints(flow, 1, out collection)); uint count; Check(collection.GetCount(out count));
                int selected = -1;
                for (uint i = 0; i < count; i++)
                {
                    IDevice endpoint = null; IStore store = null;
                    try
                    {
                        Check(collection.Item(i, out endpoint)); string id; Check(endpoint.GetId(out id));
                        bool match = id == defaultId;
                        if (name != null)
                        {
                            Check(endpoint.OpenPropertyStore(0, out store));
                            var key = new Key { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 }; Variant value;
                            Check(store.GetValue(ref key, out value));
                            try { string friendly = value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : ""; match = friendly != null && friendly.StartsWith(name, StringComparison.OrdinalIgnoreCase); }
                            finally { PropVariantClear(ref value); }
                        }
                        if (!match) continue;
                        if (selected >= 0) throw new InvalidOperationException("More than one microphone matches this input; echo cancellation cannot choose safely");
                        selected = (int)i; selectedId = id;
                    }
                    finally { Release(store); Release(endpoint); }
                }
                if (selected < 0) throw new InvalidOperationException("Audio endpoint unavailable for echo cancellation");
                return selected;
            }
            finally { Release(defaultDevice); Release(collection); Release(enumerator); }
        }
        public bool DevicesChanged()
        {
            if ((DateTime.UtcNow - checkDevices).TotalSeconds < 5) return false;
            checkDevices = DateTime.UtcNow; string id;
            Endpoint(0, null, out id); if (id != renderId) return true;
            Endpoint(1, SelectedName(device), out id); return id != captureId;
        }
        public bool Read(Action<byte[]> receive)
        {
            bool received = false;
            for (int i = 0; i < 16; i++)
            {
                buffer.SetLength(0);
                var result = new[] { new Output { Buffer = bufferPointer } }; uint status;
                Check(dmo.ProcessOutput(0, 1, result, out status));
                byte[] samples = buffer.Copy();
                if (samples.Length > 0) { received = true; receive(samples); }
                if ((result[0].Status & 0x1000000) == 0 || samples.Length == 0) break;
            }
            return received;
        }
        public void Dispose()
        {
            if (dmo != null) { try { dmo.FreeStreamingResources(); } catch { } dmo = null; }
            Release(instance); instance = null;
            if (bufferPointer != IntPtr.Zero) { Marshal.Release(bufferPointer); bufferPointer = IntPtr.Zero; }
            if (buffer != null) { buffer.Dispose(); buffer = null; }
            if (output != null) { output.Dispose(); output = null; }
        }
        private sealed class SilentOutput : IDisposable
        {
            [StructLayout(LayoutKind.Sequential)] private struct Header
            {
                public IntPtr Data; public uint Length, Recorded; public UIntPtr User;
                public uint Flags, Loops; public IntPtr Next; public UIntPtr Reserved;
            }
            [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr handle, uint device, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
            [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
            [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
            [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr handle);
            [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
            [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr handle);
            private IntPtr handle, pointer, samples;
            private readonly uint size = (uint)Marshal.SizeOf(typeof(Header));
            private static void AudioCheck(uint result) { if (result != 0) throw new InvalidOperationException("Speaker reference audio error " + result); }
            public SilentOutput()
            {
                try
                {
                    var format = new WaveFormat { Tag = 1, Channels = 1, Rate = 16000, BytesPerSecond = 32000, Align = 2, Bits = 16 };
                    AudioCheck(waveOutOpen(out handle, uint.MaxValue, ref format, IntPtr.Zero, IntPtr.Zero, 0));
                    samples = Marshal.AllocHGlobal(3200); Marshal.Copy(new byte[3200], 0, samples, 3200);
                    pointer = Marshal.AllocHGlobal((int)size);
                    Marshal.StructureToPtr(new Header { Data = samples, Length = 3200, Flags = 2 | 4, Loops = uint.MaxValue }, pointer, false);
                    AudioCheck(waveOutPrepareHeader(handle, pointer, size)); AudioCheck(waveOutWrite(handle, pointer, size));
                }
                catch { Dispose(); throw; }
            }
            public void Dispose()
            {
                if (handle != IntPtr.Zero) { waveOutReset(handle); if (pointer != IntPtr.Zero) waveOutUnprepareHeader(handle, pointer, size); waveOutClose(handle); handle = IntPtr.Zero; }
                if (pointer != IntPtr.Zero) { Marshal.FreeHGlobal(pointer); pointer = IntPtr.Zero; }
                if (samples != IntPtr.Zero) { Marshal.FreeHGlobal(samples); samples = IntPtr.Zero; }
            }
        }
    }
    // A public COM callable buffer is required by the system DMO's native IMediaBuffer ABI.
    [ComVisible(true), Guid("59EFF8B9-938C-4A26-82F2-95CB84CDC837"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICaptureBuffer
    {
        [PreserveSig] int SetLength(uint length);
        [PreserveSig] int GetMaxLength(out uint length);
        [PreserveSig] int GetBufferAndLength(IntPtr pointer, IntPtr length);
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class CaptureBuffer : ICaptureBuffer, IDisposable
    {
        private IntPtr pointer;
        private readonly uint capacity;
        private uint length;
        public CaptureBuffer(uint capacity) { this.capacity = capacity; pointer = Marshal.AllocHGlobal((int)capacity); }
        public int SetLength(uint value) { if (value > capacity) return unchecked((int)0x80070057); length = value; return 0; }
        public int GetMaxLength(out uint value) { value = capacity; return 0; }
        public int GetBufferAndLength(IntPtr data, IntPtr value) { if (data != IntPtr.Zero) Marshal.WriteIntPtr(data, pointer); if (value != IntPtr.Zero) Marshal.WriteInt32(value, (int)length); return 0; }
        public byte[] Copy() { var data = new byte[length]; if (length > 0) Marshal.Copy(pointer, data, 0, data.Length); return data; }
        public void Dispose() { if (pointer != IntPtr.Zero) { Marshal.FreeHGlobal(pointer); pointer = IntPtr.Zero; } }
    }
}

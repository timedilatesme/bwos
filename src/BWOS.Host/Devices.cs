using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace Bwos
{
    /// <summary>Cached device inventory. Only audio drivers exposing Bluetooth KS controls get connect buttons.</summary>
    internal sealed class Devices
    {
        private readonly object gate = new object();
        private Dictionary<string, object> snapshot = new Dictionary<string, object>();
        private int scanning;
        private volatile bool stopped;
        public event Action Changed;
        public void Start()
        {
            var t = new Thread(delegate()
            {
                while (!stopped) { Refresh(); for (int i = 0; i < 100 && !stopped; i++) Thread.Sleep(100); }
            }) { IsBackground = true, Name = "BWOS devices" };
            t.Start();
        }
        public void Stop() { stopped = true; }
        public object Describe() { lock (gate) return snapshot; }
        public void Refresh()
        {
            if (Interlocked.Exchange(ref scanning, 1) != 0) return;
            try
            {
                var bluetooth = BluetoothInventory.Read();
                var audio = new List<Dictionary<string, object>>();
                string error = "";
                try { audio = BluetoothAudio.Read(bluetooth); }
                catch (Exception ex) { error = "Audio inventory unavailable: " + ex.Message; }
                var next = new Dictionary<string, object> { { "bluetooth", bluetooth }, { "audio", audio }, { "error", error } };
                bool notify;
                lock (gate) { notify = Json.Serialize(next) != Json.Serialize(snapshot); snapshot = next; }
                if (notify) { var h = Changed; if (h != null) h(); }
            }
            catch (Exception ex)
            {
                lock (gate) snapshot = new Dictionary<string, object> { { "bluetooth", new object[0] }, { "audio", new object[0] }, { "error", ex.Message } };
                Log.Write("devices: " + ex.Message);
            }
            finally { Interlocked.Exchange(ref scanning, 0); }
        }
        public object Connect(string id, bool connect)
        {
            // Re-enumerate and probe capability: IDs offered by the client are never trusted.
            var endpoint = BluetoothAudio.Read(BluetoothInventory.Read()).FirstOrDefault(x => Json.Str(x, "id", "") == id && Json.Bool(x, "canControl", false));
            if (endpoint == null) return WebServer.Err("This device doesn't expose Bluetooth audio controls. Use Bluetooth settings.");
            try
            {
                BluetoothAudio.Request(id, connect);
                ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(2000); Refresh(); });
                return new Dictionary<string, object> { { "ok", true }, { "message", connect ? "Connection requested. Waiting for the speaker…" : "Disconnection requested…" } };
            }
            catch (Exception ex) { return WebServer.Err("Windows couldn't " + (connect ? "connect" : "disconnect") + " this device: " + ex.Message + ". Use Bluetooth settings."); }
        }
        public static object OpenSettings(string kind)
        {
            string uri = kind == "microphone" ? "ms-settings:privacy-microphone" : "ms-settings:bluetooth";
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return new Dictionary<string, object> { { "ok", true } };
        }
    }

    internal static class BluetoothInventory
    {
        [StructLayout(LayoutKind.Sequential)] private struct Time
        {
            public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Info
        {
            public uint Size;
            public ulong Address;
            public uint Class;
            [MarshalAs(UnmanagedType.Bool)] public bool Connected;
            [MarshalAs(UnmanagedType.Bool)] public bool Remembered;
            [MarshalAs(UnmanagedType.Bool)] public bool Authenticated;
            public Time LastSeen, LastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Search
        {
            public uint Size;
            [MarshalAs(UnmanagedType.Bool)] public bool Authenticated;
            [MarshalAs(UnmanagedType.Bool)] public bool Remembered;
            [MarshalAs(UnmanagedType.Bool)] public bool Unknown;
            [MarshalAs(UnmanagedType.Bool)] public bool Connected;
            [MarshalAs(UnmanagedType.Bool)] public bool Inquiry;
            public byte Timeout;
            public IntPtr Radio;
        }
        [DllImport("bthprops.cpl", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr BluetoothFindFirstDevice(ref Search search, ref Info info);
        [DllImport("bthprops.cpl", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BluetoothFindNextDevice(IntPtr find, ref Info info);
        [DllImport("bthprops.cpl")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BluetoothFindDeviceClose(IntPtr find);
        public static List<Dictionary<string, object>> Read()
        {
            var list = new List<Dictionary<string, object>>();
            var search = new Search { Size = (uint)Marshal.SizeOf(typeof(Search)), Authenticated = true, Remembered = true, Connected = true };
            var info = new Info { Size = (uint)Marshal.SizeOf(typeof(Info)) };
            IntPtr find = BluetoothFindFirstDevice(ref search, ref info);
            if (find != IntPtr.Zero)
            {
                try
                {
                    do
                    {
                        int major = (int)((info.Class >> 8) & 31);
                        list.Add(new Dictionary<string, object> {
                            { "id", info.Address.ToString("X12") }, { "name", info.Name }, { "connected", info.Connected },
                            { "paired", info.Authenticated || info.Remembered }, { "type", major == 4 ? "Audio" : major == 5 ? "Input device" : "Bluetooth device" },
                            { "status", info.Connected ? "Connected" : "Paired · disconnected" }
                        });
                        info.Size = (uint)Marshal.SizeOf(typeof(Info));
                    } while (BluetoothFindNextDevice(find, ref info));
                }
                finally { BluetoothFindDeviceClose(find); }
            }
            // The classic Win32 API excludes BLE. Include Windows' BLE peripheral inventory without
            // confusing PnP presence with a live Bluetooth connection.
            try
            {
                using (var query = new ManagementObjectSearcher("SELECT Name, DeviceID FROM Win32_PnPEntity WHERE PNPClass='Bluetooth'"))
                using (var results = query.Get())
                    foreach (ManagementObject device in results)
                    {
                        string id = Convert.ToString(device["DeviceID"]), name = Convert.ToString(device["Name"]);
                        if (!id.StartsWith("BTHLE\\", StringComparison.OrdinalIgnoreCase) || list.Any(x => Json.Str(x, "name", "") == name)) continue;
                        list.Add(new Dictionary<string, object> { { "id", id }, { "name", name }, { "connected", null }, { "paired", true }, { "type", "Bluetooth LE" }, { "status", "Known to Windows · connection status unavailable" } });
                    }
            }
            catch (ManagementException ex) { Log.Write("BLE inventory: " + ex.Message); }
            return list.OrderBy(x => Json.Str(x, "name", "")).ToList();
        }
    }

    internal static class BluetoothAudio
    {
        // Windows SDK COM ABI declarations (mmdeviceapi.h, devicetopology.h, ks.h).
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
        [StructLayout(LayoutKind.Explicit, Size = 24)] private struct Variant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out Key key);
            [PreserveSig] int GetValue(ref Key key, out Variant value);
        }
        [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface ITopology
        {
            [PreserveSig] int GetConnectorCount(out uint count);
            [PreserveSig] int GetConnector(uint index, out IConnector connector);
        }
        [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IConnector
        {
            [PreserveSig] int GetType(out int type);
            [PreserveSig] int GetDataFlow(out int flow);
            [PreserveSig] int ConnectTo(IConnector connector);
            [PreserveSig] int Disconnect();
            [PreserveSig] int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
            [PreserveSig] int GetConnectedTo(out IConnector connector);
            [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
        }
        [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IKs
        {
            [PreserveSig] int KsProperty(ref Property property, uint length, IntPtr data, uint dataLength, out uint returned);
        }
        [StructLayout(LayoutKind.Sequential)] private struct Property { public Guid Set; public uint Id, Flags; }
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref Variant value);
        private static readonly Guid BtAudio = new Guid("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
        private static void Release(object obj) { if (obj != null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj); }
        private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }

        private static IKs Control(IEnum enumerator, IDevice device)
        {
            object topologyObject = null, control = null;
            IConnector connector = null;
            IDevice hardware = null;
            try
            {
                Guid iid = typeof(ITopology).GUID;
                Check(device.Activate(ref iid, 23, IntPtr.Zero, out topologyObject));
                Check(((ITopology)topologyObject).GetConnector(0, out connector));
                string id;
                Check(connector.GetDeviceIdConnectedTo(out id));
                Check(enumerator.GetDevice(id, out hardware));
                iid = typeof(IKs).GUID;
                Check(hardware.Activate(ref iid, 23, IntPtr.Zero, out control));
                return (IKs)control;
            }
            finally { Release(hardware); Release(connector); Release(topologyObject); }
        }
        private static bool Supported(IKs ks)
        {
            IntPtr data = Marshal.AllocHGlobal(4);
            try
            {
                var property = new Property { Set = BtAudio, Id = 0, Flags = 0x200 /* BASICSUPPORT, no connection mutation */ };
                uint returned;
                Marshal.WriteInt32(data, 0);
                for (uint id = 0; id <= 1; id++)
                {
                    property.Id = id;
                    if (ks.KsProperty(ref property, 24, data, 4, out returned) < 0 || returned < 4 || (Marshal.ReadInt32(data) & 1) == 0) return false;
                }
                return true;
            }
            finally { Marshal.FreeHGlobal(data); }
        }
        public static List<Dictionary<string, object>> Read(List<Dictionary<string, object>> bluetooth)
        {
            IEnum enumerator = (IEnum)AudioCom.Enumerator();
            ICollection devices = null;
            var list = new List<Dictionary<string, object>>();
            try
            {
                Check(enumerator.EnumAudioEndpoints(0 /* render */, 1 | 8 /* active or unplugged */, out devices));
                uint count; Check(devices.GetCount(out count));
                for (uint i = 0; i < count; i++)
                {
                    IDevice device = null; IStore store = null; IKs ks = null;
                    try
                    {
                        Check(devices.Item(i, out device));
                        string id; uint state;
                        Check(device.GetId(out id)); Check(device.GetState(out state));
                        Check(device.OpenPropertyStore(0, out store));
                        var key = new Key { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
                        Variant name; Check(store.GetValue(ref key, out name));
                        string text;
                        try { text = name.Type == 31 ? Marshal.PtrToStringUni(name.Pointer) : "Audio device"; }
                        finally { PropVariantClear(ref name); }
                        bool supported = false;
                        try { ks = Control(enumerator, device); supported = Supported(ks); } catch (COMException) { }
                        bool known = bluetooth.Any(x => Json.Str(x, "type", "") == "Audio" && !string.IsNullOrEmpty(Json.Str(x, "name", "")) && text.IndexOf(Json.Str(x, "name", ""), StringComparison.OrdinalIgnoreCase) >= 0);
                        if (!supported && !known) continue;
                        list.Add(new Dictionary<string, object> { { "id", id }, { "name", text }, { "connected", state == 1 }, { "canControl", supported } });
                    }
                    finally { Release(ks); Release(store); Release(device); }
                }
            }
            finally { Release(devices); Release(enumerator); }
            return list;
        }
        public static void Request(string id, bool connect)
        {
            IEnum enumerator = (IEnum)AudioCom.Enumerator(); IDevice device = null; IKs ks = null;
            try
            {
                Check(enumerator.GetDevice(id, out device));
                ks = Control(enumerator, device);
                if (!Supported(ks)) throw new InvalidOperationException("The audio driver doesn't support this operation");
                var property = new Property { Set = BtAudio, Id = connect ? 0u : 1u, Flags = 1 /* GET: one-shot request */ };
                uint returned;
                Check(ks.KsProperty(ref property, 24, IntPtr.Zero, 0, out returned));
            }
            finally { Release(ks); Release(device); Release(enumerator); }
        }
    }
}

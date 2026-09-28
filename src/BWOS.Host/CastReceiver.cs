using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Bwos
{
    /// <summary>
    /// "Cast" from the YouTube app on a phone, like a smart TV. BWOS answers DIAL discovery
    /// (SSDP on the home network), so it shows up in the phone's cast menu while BWOS is running.
    /// Choosing it POSTs a pairing code; BWOS opens YouTube's TV app with that code, which links
    /// the phone and plays what was cast. Afterwards the phone controls playback directly
    /// through YouTube (the lounge), as with any TV.
    ///
    /// Safety: only the YouTube app is offered, the launched address is always www.youtube.com/tv
    /// with a filtered query, and requests from web pages (a browser Origin) are refused.
    /// </summary>
    internal sealed class CastReceiver
    {
        private const string DialService = "urn:dial-multiscreen-org:service:dial:1";
        private static readonly IPAddress SsdpGroup = IPAddress.Parse("239.255.255.250");

        private readonly int port;
        private readonly Func<bool> enabled;
        private readonly string uuid;
        private readonly string friendlyName;
        private TcpListener http;
        private Socket ssdp;
        private volatile bool stopped;

        /// <summary>A phone asked to open YouTube; the argument is the full www.youtube.com/tv URL.</summary>
        public event Action<string> Launch;
        /// <summary>The phone asked YouTube to stop (disconnected).</summary>
        public event Action Stop;

        public CastReceiver(int port, Func<bool> enabled)
        {
            this.port = port;
            this.enabled = enabled;
            friendlyName = App.CastName;
            // Stable per PC, so phones remember it as the same TV.
            using (var md5 = MD5.Create())
            {
                var b = md5.ComputeHash(Encoding.UTF8.GetBytes("BWOS-DIAL-" + Environment.MachineName));
                b[7] = (byte)((b[7] & 0x0F) | 0x30); // version 3 (Guid stores this field little-endian)
                b[8] = (byte)((b[8] & 0x3F) | 0x80);
                uuid = new Guid(b).ToString();
            }
        }

        public void Start()
        {
            // Retry in the background: right after a restart, connections from the last run can
            // still hold the port for a minute or two (TIME_WAIT).
            StartThread(delegate
            {
                for (int attempt = 1; !stopped; attempt++)
                {
                    if (TryStart()) return;
                    if (attempt == 1) Log.Write("cast receiver: port " + port + " busy, retrying in the background");
                    Thread.Sleep(Math.Min(60, 5 * attempt) * 1000);
                }
            }, "BWOS cast start");
        }

        private bool TryStart()
        {
            try
            {
                http = new TcpListener(IPAddress.Any, port);
                http.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                http.Start();
            }
            catch (SocketException)
            {
                try { http.Stop(); } catch { }
                return false;
            }
            StartThread(HttpLoop, "BWOS cast http");
            try
            {
                ssdp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                // Windows' own SSDP service also listens on 1900; sharing the port is allowed for multicast.
                ssdp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                ssdp.Bind(new IPEndPoint(IPAddress.Any, 1900));
                int joined = 0;
                foreach (var a in LanAddresses())
                {
                    try { ssdp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(SsdpGroup, a.Address)); joined++; }
                    catch (Exception ex) { Log.Write("cast: couldn't join SSDP on " + a.Address + ": " + ex.Message); }
                }
                StartThread(SsdpLoop, "BWOS cast discovery");
                Log.Write("cast receiver ready as \"" + friendlyName + "\" (port " + port + ", " + joined + " network(s))");
            }
            catch (Exception ex)
            {
                Log.Write("cast discovery unavailable (phones won't list BWOS): " + ex.Message);
            }
            return true;
        }

        public void Shutdown()
        {
            stopped = true;
            try { if (http != null) http.Stop(); } catch { }
            try { if (ssdp != null) ssdp.Close(); } catch { }
        }

        private static void StartThread(ThreadStart fn, string name)
        {
            var t = new Thread(fn);
            t.IsBackground = true;
            t.Name = name;
            t.Start();
        }

        // ------------------------------------------------------------------ discovery (SSDP)

        private void SsdpLoop()
        {
            var buf = new byte[4096];
            while (!stopped)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try { n = ssdp.ReceiveFrom(buf, ref from); }
                catch (SocketException) { if (stopped) return; continue; }
                catch (ObjectDisposedException) { return; }
                if (!enabled()) continue;

                string msg = Encoding.ASCII.GetString(buf, 0, n);
                if (!msg.StartsWith("M-SEARCH", StringComparison.OrdinalIgnoreCase)) continue;
                string st = Header(msg, "ST");
                if (st == null || (st != DialService && st != "ssdp:all")) continue;

                var remote = (IPEndPoint)from;
                var local = AddressFacing(remote.Address);
                if (local == null) continue;
                string reply =
                    "HTTP/1.1 200 OK\r\n" +
                    "CACHE-CONTROL: max-age=1800\r\n" +
                    "EXT:\r\n" +
                    "LOCATION: http://" + local + ":" + port + "/dd.xml\r\n" +
                    "SERVER: Windows/10.0 UPnP/1.1 BWOS/" + App.Version + "\r\n" +
                    "ST: " + DialService + "\r\n" +
                    "USN: uuid:" + uuid + "::" + DialService + "\r\n" +
                    "BOOTID.UPNP.ORG: 1\r\n" +
                    "CONFIGID.UPNP.ORG: 1\r\n\r\n";
                try
                {
                    using (var s = new UdpClient(new IPEndPoint(local, 0)))
                    {
                        var bytes = Encoding.ASCII.GetBytes(reply);
                        s.Send(bytes, bytes.Length, remote);
                    }
                }
                catch (Exception ex) { Log.Write("cast: SSDP reply failed: " + ex.Message); }
            }
        }

        private static string Header(string msg, string name)
        {
            foreach (var line in msg.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                int i = line.IndexOf(':');
                if (i > 0 && line.Substring(0, i).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(i + 1).Trim();
            }
            return null;
        }

        private static List<UnicastIPAddressInformation> LanAddresses()
        {
            var list = new List<UnicastIPAddressInformation>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                    if (ua.Address.ToString().StartsWith("169.254.")) continue;
                    list.Add(ua);
                }
            }
            return list;
        }

        /// <summary>This PC's address on the same subnet as the phone.</summary>
        private static IPAddress AddressFacing(IPAddress remote)
        {
            var r = remote.GetAddressBytes();
            var all = LanAddresses();
            foreach (var ua in all)
            {
                var a = ua.Address.GetAddressBytes();
                var m = ua.IPv4Mask.GetAddressBytes();
                bool same = true;
                for (int i = 0; i < 4; i++) if ((a[i] & m[i]) != (r[i] & m[i])) { same = false; break; }
                if (same) return ua.Address;
            }
            return all.Count > 0 ? all[0].Address : null;
        }

        // ------------------------------------------------------------------ DIAL REST (HTTP)

        private void HttpLoop()
        {
            while (!stopped)
            {
                TcpClient client;
                try { client = http.AcceptTcpClient(); }
                catch { return; }
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { Serve(client); }
                    catch (Exception ex) { Log.Write("cast: " + ex.Message); }
                    finally { client.Close(); }
                });
            }
        }

        private void Serve(TcpClient client)
        {
            client.ReceiveTimeout = 5000;
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, true);
            string requestLine = reader.ReadLine();
            if (string.IsNullOrEmpty(requestLine)) return;
            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return;
            string method = parts[0], path = parts[1].Split('?')[0];

            int length = 0;
            string origin = null, line;
            while (!string.IsNullOrEmpty(line = reader.ReadLine()))
            {
                int i = line.IndexOf(':');
                if (i <= 0) continue;
                string name = line.Substring(0, i).Trim(), value = line.Substring(i + 1).Trim();
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out length);
                else if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) origin = value;
            }
            string body = "";
            if (length > 0 && length <= 16 * 1024)
            {
                var buf = new char[length];
                int read = 0;
                while (read < length) { int n = reader.Read(buf, read, length - read); if (n <= 0) break; read += n; }
                body = new string(buf, 0, read);
            }

            string host = ((IPEndPoint)client.Client.LocalEndPoint).Address + ":" + port;
            if (!enabled()) { Respond(stream, 404, null, null); return; }
            // Phone apps send no Origin, a "package:" origin, or YouTube's own; a web page would send its site.
            if (origin != null && !origin.StartsWith("package:") && origin != "https://www.youtube.com")
            {
                Log.Write("cast: refused request from origin " + origin);
                Respond(stream, 403, null, null);
                return;
            }

            if (method == "GET" && (path == "/dd.xml" || path == "/"))
            {
                Respond(stream, 200, DeviceDescription(), new[] { "Application-URL: http://" + host + "/apps/" });
                return;
            }
            if (path == "/apps/YouTube" || path == "/apps/YouTube/")
            {
                if (method == "GET") { Respond(stream, 200, AppStatus(), null); return; }
                if (method == "POST")
                {
                    string url = "https://www.youtube.com/tv" + LaunchQuery(body);
                    Log.Write("cast: YouTube launch requested from " + ((IPEndPoint)client.Client.RemoteEndPoint).Address);
                    var h = Launch;
                    if (h != null) ThreadPool.QueueUserWorkItem(delegate { try { h(url); } catch (Exception ex) { Log.Write("cast launch failed: " + ex.Message); } });
                    Respond(stream, 201, null, new[] { "Location: http://" + host + "/apps/YouTube/run" });
                    return;
                }
            }
            if (method == "DELETE" && path == "/apps/YouTube/run")
            {
                var h = Stop;
                if (h != null) ThreadPool.QueueUserWorkItem(delegate { try { h(); } catch { } });
                Respond(stream, 200, null, null);
                return;
            }
            Respond(stream, 404, null, null);
        }

        /// <summary>Keeps only simple key=value pairs (pairingCode, theme, v, t…), re-encoded.</summary>
        internal static string LaunchQuery(string body)
        {
            var keep = new List<string>();
            foreach (var pair in body.Split('&'))
            {
                int i = pair.IndexOf('=');
                if (i <= 0) continue;
                string k = Uri.UnescapeDataString(pair.Substring(0, i).Replace('+', ' '));
                string v = Uri.UnescapeDataString(pair.Substring(i + 1).Replace('+', ' '));
                if (!Regex.IsMatch(k, "^[A-Za-z][A-Za-z0-9_]{0,31}$") || v.Length > 256) continue;
                if (!Regex.IsMatch(v, @"^[A-Za-z0-9_\-.:~]*$")) continue;
                keep.Add(k + "=" + Uri.EscapeDataString(v));
                if (keep.Count >= 12) break;
            }
            return keep.Count > 0 ? "?" + string.Join("&", keep) : "";
        }

        private string DeviceDescription()
        {
            return "<?xml version=\"1.0\"?>\r\n" +
                "<root xmlns=\"urn:schemas-upnp-org:device-1-0\">\r\n" +
                "  <specVersion><major>1</major><minor>0</minor></specVersion>\r\n" +
                "  <device>\r\n" +
                "    <deviceType>urn:dial-multiscreen-org:device:dial:1</deviceType>\r\n" +
                "    <friendlyName>" + SecurityElementEscape(friendlyName) + "</friendlyName>\r\n" +
                "    <manufacturer>BWOS</manufacturer>\r\n" +
                "    <modelName>BWOS</modelName>\r\n" +
                "    <UDN>uuid:" + uuid + "</UDN>\r\n" +
                "    <serviceList><service>\r\n" +
                "      <serviceType>" + DialService + "</serviceType>\r\n" +
                "      <serviceId>urn:dial-multiscreen-org:serviceId:dial</serviceId>\r\n" +
                "      <controlURL>/ssdp/notfound</controlURL><eventSubURL>/ssdp/notfound</eventSubURL><SCPDURL>/ssdp/notfound</SCPDURL>\r\n" +
                "    </service></serviceList>\r\n" +
                "  </device>\r\n" +
                "</root>\r\n";
        }

        private static string AppStatus()
        {
            // Always "stopped": the phone then sends a fresh pairing code on every cast, which BWOS
            // hands to YouTube's TV app (it may have been closed or navigated away from meanwhile).
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" +
                "<service xmlns=\"urn:dial-multiscreen-org:schemas:dial\" dialVer=\"2.1\">\r\n" +
                "  <name>YouTube</name>\r\n" +
                "  <options allowStop=\"true\"/>\r\n" +
                "  <state>stopped</state>\r\n" +
                "</service>\r\n";
        }

        private static string SecurityElementEscape(string s)
        {
            return System.Security.SecurityElement.Escape(s);
        }

        private static void Respond(Stream s, int status, string xml, string[] headers)
        {
            byte[] b = xml == null ? new byte[0] : Encoding.UTF8.GetBytes(xml);
            string reason = status == 200 ? "OK" : status == 201 ? "Created" : status == 403 ? "Forbidden" : "Not Found";
            var head = new StringBuilder("HTTP/1.1 " + status + " " + reason + "\r\n");
            if (xml != null) head.Append("Content-Type: text/xml; charset=\"utf-8\"\r\n");
            if (headers != null) foreach (var h in headers) head.Append(h + "\r\n");
            head.Append("Content-Length: " + b.Length + "\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n");
            byte[] hb = Encoding.ASCII.GetBytes(head.ToString());
            s.Write(hb, 0, hb.Length);
            s.Write(b, 0, b.Length);
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Bwos
{
    // One hidden helper holds the model in memory. WAV audio stays in memory and on loopback.
    internal sealed class WhisperEngine : IDisposable
    {
        private Process process;
        private IntPtr job;
        private string address;
        public string Backend { get; private set; }
        public double LastConfidence { get; private set; }
        public string StartupLog { get; private set; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int kind, IntPtr info, uint length);
        [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        public WhisperEngine() : this(false) { }
        internal WhisperEngine(bool cpu)
        {
            // Exercise CUDA before the microphone opens; first-use kernel compilation can be slow.
            try
            {
                Start(cpu || !WhisperAssets.HasNvidia);
                if (!cpu && Backend == "CPU" && WhisperAssets.HasNvidia) { Dispose(); Start(true); }
                TranscribeRaw(new byte[32000], "en"); LastConfidence = 0;
            }
            catch { Dispose(); try { Start(true); TranscribeRaw(new byte[32000], "en"); LastConfidence = 0; } catch { Dispose(); throw; } }
        }
        private void Start(bool cpu)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            string route = "/bwos-" + Guid.NewGuid().ToString("N");
            address = "http://127.0.0.1:" + port + route;
            var start = new ProcessStartInfo {
                FileName = Path.Combine(WhisperAssets.Runtime, "whisper-server.exe"),
                WorkingDirectory = WhisperAssets.Runtime, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                Arguments = "-m \"" + (cpu ? WhisperAssets.CpuModel : WhisperAssets.Model) + "\" --host 127.0.0.1 --port " + port + " --request-path " + route + " -t " + Math.Max(1, Math.Min(4, Environment.ProcessorCount)) + " -nfa -l auto -nf -bo 1 -bs 1 -ac 768" + (cpu ? " -ng" : "")
            };
            process = Process.Start(start);
            bool starting = true; StartupLog = "";
            // Never persist helper output: it can contain recognized speech.
            process.OutputDataReceived += delegate { };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (starting && e.Data != null && StartupLog.Length < 12000) StartupLog += e.Data + "\n"; };
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            job = CreateJobObject(IntPtr.Zero, null);
            var info = Marshal.AllocHGlobal(144);
            try
            {
                Marshal.Copy(new byte[144], 0, info, 144); Marshal.WriteInt32(info, 16, 0x2000);
                if (job == IntPtr.Zero || !SetInformationJobObject(job, 9, info, 144) || !AssignProcessToJobObject(job, process.Handle))
                    throw new InvalidOperationException("Couldn't attach the local voice helper to BWOS.");
            }
            finally { Marshal.FreeHGlobal(info); }
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < 45)
            {
                if (process.HasExited) throw new InvalidOperationException("Local voice couldn't start. Check the Windows Visual C++ runtime and GPU driver.");
                try
                {
                    var request = WebRequest.Create(address + "/health"); request.Proxy = null; request.Timeout = 1000;
                    using (var response = request.GetResponse()) using (var reader = new StreamReader(response.GetResponseStream()))
                        if (reader.ReadToEnd().Contains("\"ok\"")) { starting = false; Backend = !cpu && StartupLog.Contains("using CUDA") ? "NVIDIA GPU" : "CPU"; return; }
                }
                catch (WebException) { }
                Thread.Sleep(150);
            }
            throw new InvalidOperationException("Local voice took too long to start.");
        }
        public string Transcribe(byte[] pcm, string language)
        {
            LastConfidence = 0;
            if (pcm == null || pcm.Length < 8000 || SpeechSegmenter.Rms(pcm) < .0008) return "";
            return TranscribeRaw(pcm, language);
        }
        private string TranscribeRaw(byte[] pcm, string language)
        {
            string boundary = "bwos" + Guid.NewGuid().ToString("N");
            byte[] wav = SpeechSegmenter.Wav(pcm);
            byte[] body;
            using (var memory = new MemoryStream())
            {
                Action<string> write = text => { byte[] bytes = Encoding.UTF8.GetBytes(text); memory.Write(bytes, 0, bytes.Length); };
                foreach (var field in new[] { new[] { "response_format", "verbose_json" }, new[] { "language", language == "hi" ? "hi" : language == "auto" ? "auto" : "en" }, new[] { "translate", "false" }, new[] { "prompt", "YouTube, Netflix, Prime Video, console, कंसोल, यूट्यूब." } })
                    write("--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + field[0] + "\"\r\n\r\n" + field[1] + "\r\n");
                write("--" + boundary + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"voice.wav\"\r\nContent-Type: audio/wav\r\n\r\n");
                memory.Write(wav, 0, wav.Length); write("\r\n--" + boundary + "--\r\n"); body = memory.ToArray();
            }
            var request = (HttpWebRequest)WebRequest.Create(address + "/inference");
            request.Method = "POST"; request.Proxy = null; request.Timeout = 45000; request.ReadWriteTimeout = 45000;
            request.ContentType = "multipart/form-data; boundary=" + boundary; request.ContentLength = body.Length;
            using (var stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
            using (var response = request.GetResponse()) using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                var result = Json.ParseObject(reader.ReadToEnd());
                double sum = 0; int count = 0;
                var text = new StringBuilder();
                foreach (var segment in Json.List(result, "segments"))
                {
                    object noSpeech, logprob;
                    if (segment.TryGetValue("no_speech_prob", out noSpeech) && Convert.ToDouble(noSpeech) > .65) continue;
                    if (segment.TryGetValue("avg_logprob", out logprob) && Convert.ToDouble(logprob) < -1.0) continue;
                    text.Append(Json.Str(segment, "text", ""));
                    if (segment.TryGetValue("avg_logprob", out logprob)) { sum += Math.Exp(Convert.ToDouble(logprob)); count++; }
                }
                LastConfidence = count == 0 ? 0 : sum / count;
                return text.ToString().Trim();
            }
        }
        public void Dispose()
        {
            if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
            if (process != null) { try { if (!process.HasExited) process.Kill(); } catch { } process.Dispose(); process = null; }
        }
    }
}

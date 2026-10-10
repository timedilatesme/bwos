using System;
using System.IO;
using System.IO.Compression;
using System.Net;

namespace Bwos
{
    internal static class VoiceAssets
    {
        public const string English = "vosk-model-small-en-us-0.15";
        public const string IndianEnglish = "vosk-model-small-en-in-0.4";
        public const string Hindi = "vosk-model-small-hi-0.22";
        public static string Root
        {
            get
            {
                string bundled = Path.Combine(Paths.AppDir, "voice");
                return File.Exists(Path.Combine(bundled, "libvosk.dll")) ? bundled : Path.Combine(Paths.DataDir, @"tools\voice");
            }
        }
        public static string Model(string language) { return Path.Combine(Root, language == "hi" ? Hindi : language == "en-in" ? IndianEnglish : English); }
        public static bool Available(string language)
        {
            return Environment.Is64BitProcess && File.Exists(Path.Combine(Root, "libvosk.dll")) && File.Exists(Path.Combine(Model(language), @"am\final.mdl"));
        }

        // Explicitly initiated from Settings. Audio never leaves this PC; only assets are downloaded.
        public static void Install(Action<string> progress)
        {
            if (!Environment.Is64BitProcess) throw new InvalidOperationException("Offline voice needs 64-bit Windows.");
            string root = Path.Combine(Paths.DataDir, @"tools\voice");
            Directory.CreateDirectory(root);
            string stage = Path.Combine(root, "download-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                if (!File.Exists(Path.Combine(root, "libvosk.dll")))
                {
                    Download("https://github.com/alphacep/vosk-api/releases/download/v0.3.45/vosk-win64-0.3.45.zip", Path.Combine(stage, "runtime.zip"), "Voice engine", progress);
                    Extract(Path.Combine(stage, "runtime.zip"), Path.Combine(stage, "runtime"));
                    foreach (string dll in Directory.GetFiles(Path.Combine(stage, "runtime"), "*.dll", SearchOption.AllDirectories))
                        File.Copy(dll, Path.Combine(root, Path.GetFileName(dll)), true);
                    if (!File.Exists(Path.Combine(root, "libvosk.dll"))) throw new InvalidDataException("The voice engine archive did not contain libvosk.dll.");
                    File.WriteAllText(Path.Combine(root, "LICENSE.txt"), "Vosk API: Apache-2.0, https://github.com/alphacep/vosk-api/blob/master/COPYING\r\nEnglish and Hindi small models: Apache-2.0, https://alphacephei.com/vosk/models\r\nNative library dependencies retain their respective licenses; see THIRD_PARTY_NOTICES.md shipped with BWOS.");
                }
                foreach (string name in new[] { English, Hindi, IndianEnglish })
                {
                    if (File.Exists(Path.Combine(root, name, @"am\final.mdl"))) continue;
                    string zip = Path.Combine(stage, name + ".zip");
                    Download("https://alphacephei.com/vosk/models/" + name + ".zip", zip, name == Hindi ? "Hindi model" : name == IndianEnglish ? "Indian English model" : "US English model", progress);
                    Extract(zip, stage);
                    string destination = Path.Combine(root, name);
                    // Never overwrite a model while the recognizer might have it open.
                    if (Directory.Exists(destination)) throw new IOException("An incomplete voice model exists. Remove " + destination + " and try again.");
                    Directory.Move(Path.Combine(stage, name), destination);
                }
                progress("Ready — US English, Indian English and Hindi installed");
            }
            finally { try { Directory.Delete(stage, true); } catch { } }
        }

        internal static void Download(string url, string target, string label, Action<string> progress)
        {
            progress("Downloading " + label + "…");
            using (var client = new WebClient())
            {
                client.Headers["User-Agent"] = "BWOS/" + App.Version;
                using (var done = new System.Threading.ManualResetEvent(false))
                {
                    Exception failure = null; int last = -1;
                    client.DownloadProgressChanged += delegate(object sender, DownloadProgressChangedEventArgs e)
                    {
                        int step = e.ProgressPercentage / 5;
                        if (step != last) { last = step; progress("Downloading " + label + "… " + e.ProgressPercentage + "%"); }
                    };
                    client.DownloadFileCompleted += delegate(object sender, System.ComponentModel.AsyncCompletedEventArgs e) { failure = e.Error; if (e.Cancelled) failure = new OperationCanceledException(); done.Set(); };
                    client.DownloadFileAsync(new Uri(url), target); done.WaitOne();
                    if (failure != null) throw failure;
                }
            }
        }

        internal static void Extract(string zip, string destination)
        {
            string root = Path.GetFullPath(destination).TrimEnd('\\') + "\\";
            Directory.CreateDirectory(root);
            using (var archive = ZipFile.OpenRead(zip))
                foreach (var entry in archive.Entries)
                {
                    string path = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe archive path.");
                    if (entry.Name.Length == 0) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    entry.ExtractToFile(path, true);
                }
        }
    }
}

using System;
using System.IO;
using System.Security.Cryptography;

namespace Bwos
{
    internal static class WhisperAssets
    {
        public const string ModelName = "ggml-large-v3-turbo-q5_0.bin";
        public const string CpuModelName = "ggml-base-q5_1.bin";
        public static string Root { get { return Path.Combine(Paths.DataDir, @"tools\whisper"); } }
        public static string Runtime { get { return Path.Combine(Root, @"runtime-v1.9.2\Release"); } }
        public static string Model { get { return Path.Combine(Root, ModelName); } }
        public static string CpuModel { get { return Path.Combine(Root, CpuModelName); } }
        public static bool HasNvidia { get { return File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvcuda.dll")); } }
        public static bool Available { get { return Environment.Is64BitProcess && File.Exists(Path.Combine(Runtime, "whisper-server.exe")) && Valid(CpuModel, 59707625) && (!HasNvidia || Valid(Model, 574041195)); } }
        private static bool Valid(string path, long length) { return File.Exists(path) && new FileInfo(path).Length == length; }
        public static void Install(Action<string> progress)
        {
            if (!Environment.Is64BitProcess) throw new InvalidOperationException("Local Whisper needs 64-bit Windows.");
            Directory.CreateDirectory(Root);
            string stage = Path.Combine(Root, "download-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                if (!File.Exists(Path.Combine(Runtime, "whisper-server.exe")))
                {
                    string zip = Path.Combine(stage, "runtime.zip");
                    VoiceAssets.Download("https://github.com/ggml-org/whisper.cpp/releases/download/v1.9.2/" + (HasNvidia ? "whisper-cublas-11.8.0-bin-x64.zip" : "whisper-bin-x64.zip"), zip, "local voice runtime", progress);
                    VoiceAssets.Extract(zip, Path.Combine(stage, "runtime"));
                    string dest = Path.Combine(Root, "runtime-v1.9.2");
                    if (Directory.Exists(dest)) throw new IOException("Incomplete Whisper runtime: " + dest);
                    Directory.Move(Path.Combine(stage, "runtime"), dest);
                }
                if (HasNvidia && !Valid(Model, 574041195))
                {
                    string target = Path.Combine(stage, ModelName);
                    VoiceAssets.Download("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + ModelName, target, "multilingual Whisper model (574 MB)", progress);
                    if (new FileInfo(target).Length != 574041195) throw new InvalidDataException("The Whisper download is incomplete.");
                    File.Copy(target, Model, true);
                }
                if (!Valid(CpuModel, 59707625))
                {
                    string target = Path.Combine(stage, CpuModelName);
                    VoiceAssets.Download("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + CpuModelName, target, "CPU voice model (60 MB)", progress);
                    if (!Valid(target, 59707625)) throw new InvalidDataException("The CPU voice model download is incomplete.");
                    File.Copy(target, CpuModel, true);
                }
                if (HasNvidia && !File.Exists(Path.Combine(Runtime, "cublas64_11.dll")))
                {
                    string zip = Path.Combine(stage, "cublas.zip");
                    VoiceAssets.Download("https://developer.download.nvidia.com/compute/cuda/redist/libcublas/windows-x86_64/libcublas-windows-x86_64-11.11.3.6-archive.zip", zip, "NVIDIA acceleration (421 MB)", progress);
                    using (var hash = SHA256.Create()) using (var stream = File.OpenRead(zip))
                        if (BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() != "67b0934a6359e4ee26fff823c356021589d392c4fd49ca12624f570edc08e2b9") throw new InvalidDataException("NVIDIA package checksum mismatch.");
                    VoiceAssets.Extract(zip, Path.Combine(stage, "cublas"));
                    foreach (var dll in Directory.GetFiles(Path.Combine(stage, "cublas"), "*.dll", SearchOption.AllDirectories)) File.Copy(dll, Path.Combine(Runtime, Path.GetFileName(dll)), true);
                    foreach (var license in Directory.GetFiles(Path.Combine(stage, "cublas"), "LICENSE*", SearchOption.AllDirectories)) File.Copy(license, Path.Combine(Root, "NVIDIA-LICENSE.txt"), true);
                }
                if (!Available) throw new InvalidDataException("Local Whisper files are incomplete. Download again after removing the incomplete model.");
                File.WriteAllText(Path.Combine(Root, "WHISPER-LICENSE.txt"), "MIT License\r\n\r\nCopyright (c) 2023-2026 The ggml authors\r\nCopyright (c) 2022 OpenAI\r\n\r\nPermission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the \"Software\"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:\r\n\r\nThe above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.\r\n\r\nTHE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.\r\n\r\nSources: https://github.com/ggml-org/whisper.cpp and https://github.com/openai/whisper\r\n");
                progress("Ready — free local voice installed");
            }
            finally
            {
                // Only this installation's generated directory is eligible for cleanup.
                string allowed = Path.GetFullPath(Root).TrimEnd('\\') + "\\";
                if (Path.GetFullPath(stage).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                    try { Directory.Delete(stage, true); } catch { }
            }
        }
    }
}

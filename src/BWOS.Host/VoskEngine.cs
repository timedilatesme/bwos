using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Bwos
{
    /// <summary>Loads the optional native runtime by absolute path; no NuGet or Python required.</summary>
    internal sealed class VoskEngine : IDisposable
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ModelNew(IntPtr path);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Free(IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr RecognizerNew(IntPtr model, float rate);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Accept(IntPtr recognizer, byte[] samples, int length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Result(IntPtr recognizer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetWords(IntPtr recognizer, int enabled);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetLog(int level);
        private IntPtr library, model, recognizer;
        private Free modelFree, recognizerFree;
        private Accept accept;
        private Result result;
        private Free reset;
        public string LastText { get; private set; }
        public double LastConfidence { get; private set; }
        public VoskEngine(string language)
        {
            try
            {
                library = LoadLibraryEx(Path.Combine(VoiceAssets.Root, "libvosk.dll"), IntPtr.Zero, 0x100 | 0x1000);
                if (library == IntPtr.Zero) throw new InvalidOperationException("Couldn't load the voice engine (Windows error " + Marshal.GetLastWin32Error() + "). Reinstall offline voice.");
                modelFree = Function<Free>("vosk_model_free");
                recognizerFree = Function<Free>("vosk_recognizer_free");
                Function<SetLog>("vosk_set_log_level")(-1);
                byte[] path = Encoding.UTF8.GetBytes(VoiceAssets.Model(language) + "\0");
                var memory = Marshal.AllocHGlobal(path.Length);
                try { Marshal.Copy(path, 0, memory, path.Length); model = Function<ModelNew>("vosk_model_new")(memory); }
                finally { Marshal.FreeHGlobal(memory); }
                if (model == IntPtr.Zero) throw new InvalidOperationException("Couldn't open the " + language + " voice model.");
                recognizer = Function<RecognizerNew>("vosk_recognizer_new")(model, 16000);
                if (recognizer == IntPtr.Zero) throw new InvalidOperationException("Couldn't start voice recognition.");
                Function<SetWords>("vosk_recognizer_set_words")(recognizer, 1);
                accept = Function<Accept>("vosk_recognizer_accept_waveform");
                result = Function<Result>("vosk_recognizer_result");
                reset = Function<Free>("vosk_recognizer_reset");
            }
            catch { Dispose(); throw; }
        }
        private T Function<T>(string name) where T : class
        {
            IntPtr address = GetProcAddress(library, name);
            if (address == IntPtr.Zero) throw new InvalidOperationException("Missing voice engine function: " + name);
            return Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
        }
        public string Feed(byte[] samples)
        {
            LastText = null;
            int status = accept(recognizer, samples, samples.Length);
            if (status < 0) throw new InvalidOperationException("Voice recognition failed.");
            if (status == 0) return null;
            IntPtr p = result(recognizer);
            int length = 0;
            while (Marshal.ReadByte(p, length) != 0) length++;
            byte[] bytes = new byte[length];
            Marshal.Copy(p, bytes, 0, length);
            var json = Json.ParseObject(Encoding.UTF8.GetString(bytes));
            var words = Json.List(json, "result");
            double confidence = 0;
            foreach (var word in words) { object c; if (word.TryGetValue("conf", out c)) confidence += Convert.ToDouble(c); }
            LastText = Json.Str(json, "text", "");
            LastConfidence = words.Count == 0 ? 0 : confidence / words.Count;
            // Reject low-confidence noise before it can become a UI command.
            if (words.Count == 0 || LastConfidence < 0.65) return null;
            return LastText;
        }
        public void Reset() { reset(recognizer); }
        public void Dispose()
        {
            if (recognizer != IntPtr.Zero) { recognizerFree(recognizer); recognizer = IntPtr.Zero; }
            if (model != IntPtr.Zero) { modelFree(model); model = IntPtr.Zero; }
            if (library != IntPtr.Zero) { FreeLibrary(library); library = IntPtr.Zero; }
        }
    }
}

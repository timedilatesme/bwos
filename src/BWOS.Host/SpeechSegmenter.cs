using System;
using System.Collections.Generic;
using System.IO;

namespace Bwos
{
    // A short pause ends an utterance. Pre-roll keeps the beginning of quiet words intact.
    internal sealed class SpeechSegmenter
    {
        private readonly Queue<byte[]> lead = new Queue<byte[]>();
        private readonly MemoryStream utterance = new MemoryStream();
        private int quietBytes, voicedBytes, leadBytes;
        private double noise = 0.0005;
        public bool Active { get { return utterance.Length > 0; } }
        public int MaximumBytes = 384000;
        public byte[] Feed(byte[] pcm)
        {
            double rms = Rms(pcm);
            bool voice = rms > Math.Max(0.0012, noise * 2.8);
            double decay = Math.Pow(.98, pcm.Length / 3200.0);
            if (!Active && !voice) noise = noise * decay + Math.Min(rms, .008) * (1 - decay);
            if (!Active)
            {
                lead.Enqueue(pcm); leadBytes += pcm.Length;
                while (lead.Count > 1 && leadBytes - lead.Peek().Length >= 12800) leadBytes -= lead.Dequeue().Length;
                if (!voice) return null;
                foreach (var block in lead) utterance.Write(block, 0, block.Length);
                lead.Clear(); leadBytes = 0;
            }
            else utterance.Write(pcm, 0, pcm.Length);
            if (voice) { voicedBytes += pcm.Length; quietBytes = 0; }
            else quietBytes += pcm.Length;
            bool limited = utterance.Length >= Math.Max(32000, MaximumBytes);
            if (quietBytes < 22400 && !limited) return null; // 700 ms pause / configurable limit
            byte[] result = voicedBytes >= 8000 ? utterance.ToArray() : null; // skip clicks and very short noise
            byte[] tail = limited && result != null ? new byte[Math.Min(12800, result.Length)] : null;
            if (tail != null) Buffer.BlockCopy(result, result.Length - tail.Length, tail, 0, tail.Length);
            Reset();
            // Keep a wake phrase intact if it straddles two continuous-sound chunks.
            if (tail != null) { lead.Enqueue(tail); leadBytes = tail.Length; }
            return result;
        }
        public void Reset() { utterance.SetLength(0); lead.Clear(); quietBytes = voicedBytes = leadBytes = 0; }
        internal static double Rms(byte[] samples)
        {
            if (samples.Length < 2) return 0;
            double sum = 0;
            for (int i = 0; i + 1 < samples.Length; i += 2) { double s = (short)(samples[i] | samples[i + 1] << 8) / 32768.0; sum += s * s; }
            return Math.Sqrt(sum / (samples.Length / 2));
        }
        internal static byte[] Wav(byte[] pcm)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(pcm.Length + 36);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
                writer.Write((short)2); writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(pcm.Length); writer.Write(pcm);
                return stream.ToArray();
            }
        }
    }
}

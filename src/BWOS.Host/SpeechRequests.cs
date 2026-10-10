using System;
using System.Collections.Generic;

namespace Bwos
{
    // Keep a wake phrase and its immediate follow-up while local inference is busy.
    // Bound memory and age so a slow PC never executes a long backlog of old speech.
    internal sealed class SpeechRequests
    {
        internal sealed class Request { public byte[] Audio; public DateTime At; }
        private readonly Queue<Request> pending = new Queue<Request>();
        public void Add(byte[] audio, DateTime now)
        {
            lock (pending)
            {
                Expire(now);
                if (pending.Count >= 3) pending.Dequeue();
                pending.Enqueue(new Request { Audio = audio, At = now });
            }
        }
        public Request Take(DateTime now)
        {
            lock (pending) { Expire(now); return pending.Count == 0 ? null : pending.Dequeue(); }
        }
        public void Clear() { lock (pending) pending.Clear(); }
        private void Expire(DateTime now)
        {
            while (pending.Count > 0 && (now - pending.Peek().At).TotalSeconds > 8) pending.Dequeue();
        }
    }
}

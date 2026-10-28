using System;
using System.Threading;

namespace Bwos
{
    internal static class BackgroundWork
    {
        public static void Queue(string operation, Action action)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { action(); }
                catch (Exception ex) { Log.Write(operation + " failed: " + ex); }
            });
        }
    }
}

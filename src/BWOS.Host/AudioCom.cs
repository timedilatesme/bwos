using System;

namespace Bwos
{
    internal static class AudioCom
    {
        // All audio features share the COM runtime type. Separate imported coclasses
        // for the same CLSID can collide when Windows returns a cached RCW.
        public static object Enumerator()
        {
            return Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
        }
    }
}

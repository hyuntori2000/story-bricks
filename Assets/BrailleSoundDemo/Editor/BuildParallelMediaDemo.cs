using UnityEngine;

namespace BrailleSoundDemo.Editor
{
    public static class BuildParallelMediaDemo
    {
        // Offline checks run before either player build. No provider credentials are used.
        public static void VerifyAndBuildMac()
        {
            Tests.SoundPaletteVerification.Run();
            Tests.ImagePipelineVerification.Run();
            BuildDemo.BuildMac();
            Debug.Log("PARALLEL_MEDIA_MAC_BUILD_OK");
        }
    }
}

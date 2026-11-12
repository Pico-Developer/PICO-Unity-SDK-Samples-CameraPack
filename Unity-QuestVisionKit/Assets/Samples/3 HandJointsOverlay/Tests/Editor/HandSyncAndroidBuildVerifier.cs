using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace PICO.Samples.HandJointsOverlay.Tests
{
    public static class HandSyncAndroidBuildVerifier
    {
        public static void Build()
        {
            string outputPath = Environment.GetEnvironmentVariable("HAND_SYNC_BUILD_PATH");
            if (string.IsNullOrEmpty(outputPath))
            {
                throw new BuildFailedException("HAND_SYNC_BUILD_PATH is not set.");
            }
            string directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            InsecureHttpOption previousHttpOption =
                PlayerSettings.insecureHttpOption;
            try
            {
                PlayerSettings.insecureHttpOption =
                    InsecureHttpOption.AlwaysAllowed;
                BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[]
                    {
                        "Assets/Samples/3 HandJointsOverlay/HandJointsOverlayVisualizer.unity"
                    },
                    locationPathName = outputPath,
                    target = BuildTarget.Android,
                    options = BuildOptions.Development
                });
                if (report.summary.result != BuildResult.Succeeded)
                {
                    throw new BuildFailedException(
                        $"Android build failed: {report.summary.result}");
                }
            }
            finally
            {
                PlayerSettings.insecureHttpOption = previousHttpOption;
            }
        }
    }
}

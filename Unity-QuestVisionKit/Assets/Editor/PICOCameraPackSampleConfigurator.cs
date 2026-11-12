#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using UnityEditor;
using UnityEditor.PackageManager.UI;
using UnityEngine;

namespace PICO.Editor
{
    public sealed class PICOSampleDependency
    {
        public PICOSampleDependency(
            string id,
            string displayName,
            string importedPath,
            string packageName,
            string sampleDisplayName,
            string packageVersion = null,
            string purpose = null)
        {
            Id = id;
            DisplayName = displayName;
            ImportedPath = importedPath;
            PackageName = packageName;
            SampleDisplayName = sampleDisplayName;
            PackageVersion = packageVersion;
            Purpose = purpose ?? string.Empty;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string ImportedPath { get; }
        public string PackageName { get; }
        public string SampleDisplayName { get; }
        public string PackageVersion { get; }
        public string Purpose { get; }
    }

    public sealed class PICOSampleDependencyStatus
    {
        public PICOSampleDependencyStatus(
            string displayName,
            string source,
            string importedPath,
            string purpose,
            string status)
        {
            DisplayName = displayName;
            Source = source;
            ImportedPath = importedPath;
            Purpose = purpose;
            Status = status;
        }

        public string DisplayName { get; }
        public string Source { get; }
        public string ImportedPath { get; }
        public string Purpose { get; }
        public string Status { get; }
    }

    public static class PICOCameraPackSampleConfigurator
    {
        public const string WindowMenuPath = "PICO/Camera Sample/Configure Dependencies";
        public const string ZXingMenuPath = "PICO/Camera Sample/Add ZXing Lib";
        public const string UpdateZXingDefineMenuPath = "PICO/Camera Sample/Update ZXing Define Symbol";
        public const int ConfigureDependenciesMenuPriority = 1000;
        public const int ZXingMenuPriority = 2001;
        public const int UpdateZXingDefineMenuPriority = ZXingMenuPriority + 1;

        private const string k_zxingVersion = "0.16.9";
        private const string k_zxingPackageUrlFormat = "https://www.nuget.org/api/v2/package/ZXing.Net/{0}";
        private const string k_zxingFrameworkPath = "lib/netstandard2.0/zxing.dll";
        private const string k_zxingDllName = "zxing.dll";
        private const string k_promptShownKey = "PICO.CameraPack.SampleDependenciesPromptShown";

        [MenuItem(WindowMenuPath, false, ConfigureDependenciesMenuPriority)]
        public static void OpenWindow()
        {
            PICOCameraPackSampleConfiguratorWindow.Open();
        }

        [MenuItem(ZXingMenuPath, false, ZXingMenuPriority)]
        public static void AddZXingLib()
        {
            InstallZXingLib(overwrite: true);
        }

        public static IReadOnlyList<PICOSampleDependency> GetDefaultDependencies()
        {
            return new[]
            {
                new PICOSampleDependency(
                    "xr-hands-hand-visualizer",
                    "XR Hands HandVisualizer",
                    "Assets/Samples/XR Hands/1.8.1/HandVisualizer",
                    "com.unity.xr.hands",
                    "HandVisualizer",
                    "1.8.1",
                    "Provides hand mesh and joint visualization assets used by HandJointsOverlay."),
                new PICOSampleDependency(
                    "xri-starter-assets",
                    "XR Interaction Toolkit Starter Assets",
                    "Assets/Samples/XR Interaction Toolkit/3.3.1/Starter Assets",
                    "com.unity.xr.interaction.toolkit",
                    "Starter Assets",
                    "3.3.1",
                    "Provides input actions and interaction presets used by XR sample scenes."),
                new PICOSampleDependency(
                    "zxing-net",
                    "ZXing.Net",
                    "Assets/Samples/2 QRCodeTracking/Plugins",
                    null,
                    null,
                    null,
                    "Provides the QR code decoder DLL required by QRCodeTracking."),
            };
        }

        public static int GetMissingDependencyCount(
            IEnumerable<PICOSampleDependency> dependencies,
            Func<string, bool> pathExists)
        {
            if (dependencies == null)
            {
                throw new ArgumentNullException(nameof(dependencies));
            }

            if (pathExists == null)
            {
                throw new ArgumentNullException(nameof(pathExists));
            }

            return dependencies.Count(dependency => !pathExists(dependency.ImportedPath));
        }

        public static int GetMissingDependencyCount()
        {
            return GetDefaultDependencies().Count(dependency => !IsDependencyImported(dependency));
        }

        public static IReadOnlyList<PICOSampleDependencyStatus> GetDependencyStatuses(
            IEnumerable<PICOSampleDependency> dependencies,
            Func<string, bool> pathExists)
        {
            if (dependencies == null)
            {
                throw new ArgumentNullException(nameof(dependencies));
            }

            if (pathExists == null)
            {
                throw new ArgumentNullException(nameof(pathExists));
            }

            return dependencies
                .Select(dependency => new PICOSampleDependencyStatus(
                    dependency.DisplayName,
                    GetDependencySource(dependency),
                    dependency.ImportedPath,
                    dependency.Purpose,
                    pathExists(dependency.ImportedPath) ? "Installed" : "Missing"))
                .ToArray();
        }

        public static IReadOnlyList<PICOSampleDependencyStatus> GetDependencyStatuses()
        {
            return GetDefaultDependencies()
                .Select(dependency => new PICOSampleDependencyStatus(
                    dependency.DisplayName,
                    GetDependencySource(dependency),
                    dependency.ImportedPath,
                    dependency.Purpose,
                    IsDependencyImported(dependency) ? "Installed" : "Missing"))
                .ToArray();
        }

        public static void ConfigureAllDependencies()
        {
            try
            {
                EditorUtility.DisplayProgressBar(
                    "PICO Camera Pack",
                    "Importing Unity package samples...",
                    0.2f);

                foreach (var dependency in GetDefaultDependencies())
                {
                    if (dependency.Id == "zxing-net")
                    {
                        continue;
                    }

                    ImportUnitySample(dependency);
                }

                EditorUtility.DisplayProgressBar(
                    "PICO Camera Pack",
                    "Installing ZXing.Net...",
                    0.65f);
                InstallZXingLib(overwrite: true);

                EditorUtility.DisplayProgressBar(
                    "PICO Camera Pack",
                    "Refreshing project...",
                    0.9f);
                AssetDatabase.Refresh();

                EditorUtility.DisplayDialog(
                    "PICO Camera Pack",
                    "Dependency samples and QRCodeTracking library have been configured.",
                    "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PICOCameraPackSampleConfigurator] Failed to configure dependencies: {ex}");
                EditorUtility.DisplayDialog(
                    "PICO Camera Pack",
                    $"Failed to configure dependencies:\n{ex.Message}",
                    "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        public static void PromptOnFirstOpenIfNeeded()
        {
            bool promptAlreadyShown = EditorPrefs.GetBool(k_promptShownKey, false);
            if (!ShouldPromptOnStartup(promptAlreadyShown, GetMissingDependencyCount(), Application.isBatchMode))
            {
                return;
            }

            EditorPrefs.SetBool(k_promptShownKey, true);

            bool openWindow = EditorUtility.DisplayDialog(
                "PICO Camera Pack",
                "Optional Unity sample dependencies are not imported. Open the configuration page to import all dependencies?",
                "Open",
                "Later");

            if (openWindow)
            {
                OpenWindow();
            }
        }

        public static bool ShouldPromptOnStartup(
            bool promptAlreadyShown,
            int missingDependencyCount,
            bool isBatchMode)
        {
            return !isBatchMode && !promptAlreadyShown && missingDependencyCount > 0;
        }

        private static bool IsDependencyImported(PICOSampleDependency dependency)
        {
            if (dependency.Id == "zxing-net")
            {
                return File.Exists(GetZXingDllPath());
            }

            return AssetDatabase.IsValidFolder(dependency.ImportedPath);
        }

        private static string GetDependencySource(PICOSampleDependency dependency)
        {
            if (string.IsNullOrEmpty(dependency.PackageName))
            {
                return "Manual dependency";
            }

            string package = string.IsNullOrEmpty(dependency.PackageVersion)
                ? dependency.PackageName
                : $"{dependency.PackageName}@{dependency.PackageVersion}";

            return string.IsNullOrEmpty(dependency.SampleDisplayName)
                ? package
                : $"{package} / {dependency.SampleDisplayName}";
        }

        private static void ImportUnitySample(PICOSampleDependency dependency)
        {
            var samples = Sample.FindByPackage(dependency.PackageName, dependency.PackageVersion);
            var sample = samples.FirstOrDefault(item => item.displayName == dependency.SampleDisplayName);

            if (string.IsNullOrEmpty(sample.displayName))
            {
                throw new InvalidOperationException(
                    $"Could not find sample '{dependency.SampleDisplayName}' in package '{dependency.PackageName}@{dependency.PackageVersion}'.");
            }

            sample.Import(Sample.ImportOptions.OverridePreviousImports);
            Debug.Log($"[PICOCameraPackSampleConfigurator] Imported {dependency.DisplayName}.");
        }

        public static void InstallZXingLib(bool overwrite)
        {
            var destinationDll = GetZXingDllPath();
            var destinationDir = Path.GetDirectoryName(destinationDll);

            if (File.Exists(destinationDll) && !overwrite)
            {
                return;
            }

            var tempNupkg = Path.Combine(Path.GetTempPath(), $"ZXing.Net.{k_zxingVersion}.nupkg");

            try
            {
                DownloadNupkg(tempNupkg);
                Directory.CreateDirectory(destinationDir);
                ExtractDll(tempNupkg, destinationDll);
                AssetDatabase.Refresh();
                ZXingDefineSymbolChecker.UpdateZXingDefine();

                Debug.Log($"[PICOCameraPackSampleConfigurator] ZXing.Net {k_zxingVersion} installed to {GetZXingDependencyPath()}/{k_zxingDllName}");
            }
            finally
            {
                if (File.Exists(tempNupkg))
                {
                    File.Delete(tempNupkg);
                }
            }
        }

        private static string GetZXingDependencyPath()
        {
            return GetDefaultDependencies().Single(dependency => dependency.Id == "zxing-net").ImportedPath;
        }

        private static string GetZXingDllPath()
        {
            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(
                projectRoot,
                GetZXingDependencyPath().Replace('/', Path.DirectorySeparatorChar),
                k_zxingDllName);
        }

        private static void DownloadNupkg(string targetPath)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            using (var client = new WebClient())
            {
                client.DownloadFile(string.Format(k_zxingPackageUrlFormat, k_zxingVersion), targetPath);
            }
        }

        private static void ExtractDll(string nupkgPath, string destinationDll)
        {
            using (var archive = ZipFile.OpenRead(nupkgPath))
            {
                var entry = archive.Entries.FirstOrDefault(
                    item => item.FullName.Replace('\\', '/').EndsWith(k_zxingFrameworkPath, StringComparison.OrdinalIgnoreCase));

                if (entry == null)
                {
                    throw new FileNotFoundException($"Could not find '{k_zxingFrameworkPath}' inside the NuGet package.");
                }

                entry.ExtractToFile(destinationDll, overwrite: true);
            }
        }
    }

    public sealed class PICOCameraPackSampleConfiguratorWindow : EditorWindow
    {
        private Vector2 scrollPosition;

        public static void Open()
        {
            var window = GetWindow<PICOCameraPackSampleConfiguratorWindow>("PICO Camera Sample");
            window.minSize = new Vector2(520f, 420f);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("PICO Camera Sample Dependencies", EditorStyles.boldLabel);
            EditorGUILayout.Space(6f);

            int missingCount = PICOCameraPackSampleConfigurator.GetMissingDependencyCount();
            string status = missingCount == 0
                ? "All optional dependencies are configured."
                : $"{missingCount} optional dependencies need configuration.";
            EditorGUILayout.HelpBox(status, missingCount == 0 ? MessageType.Info : MessageType.Warning);

            EditorGUILayout.Space(8f);
            EditorGUILayout.HelpBox(
                "This tool imports optional Unity samples and the ZXing decoder required by the PICO Camera Sample scenes. PICO Camera Pack runtime support is provided by the PICO Unity SDK.",
                MessageType.None);

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Dependency Details", EditorStyles.boldLabel);
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            foreach (var dependency in PICOCameraPackSampleConfigurator.GetDependencyStatuses())
            {
                DrawDependencyStatus(dependency);
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.Space(8f);

            if (GUILayout.Button("Import / Configure All Dependencies", GUILayout.Height(32f)))
            {
                PICOCameraPackSampleConfigurator.ConfigureAllDependencies();
            }
        }

        private static void DrawDependencyStatus(PICOSampleDependencyStatus dependency)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(dependency.DisplayName, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(dependency.Status, GUILayout.Width(80f));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField($"Source: {dependency.Source}", EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField($"Target: {dependency.ImportedPath}", EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField($"Used by: {dependency.Purpose}", EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndVertical();
        }
    }

    [InitializeOnLoad]
    internal static class PICOCameraPackDependencyStartupPrompt
    {
        static PICOCameraPackDependencyStartupPrompt()
        {
            EditorApplication.delayCall += PICOCameraPackSampleConfigurator.PromptOnFirstOpenIfNeeded;
        }
    }
}
#endif

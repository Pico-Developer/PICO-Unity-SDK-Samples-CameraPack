using System.Linq;
using NUnit.Framework;

namespace PICO.Editor.Tests
{
    public class PICOCameraPackSampleConfiguratorTests
    {
        [Test]
        public void MenuPaths_UseCameraSampleTopLevelMenu()
        {
            Assert.That(PICOCameraPackSampleConfigurator.WindowMenuPath, Is.EqualTo("PICO/Camera Sample/Configure Dependencies"));
            Assert.That(PICOCameraPackSampleConfigurator.ZXingMenuPath, Is.EqualTo("PICO/Camera Sample/Add ZXing Lib"));
            Assert.That(PICOCameraPackSampleConfigurator.UpdateZXingDefineMenuPath, Is.EqualTo("PICO/Camera Sample/Update ZXing Define Symbol"));
        }

        [Test]
        public void MenuPriorities_GroupZXingItemsBelowDependencyConfiguration()
        {
            Assert.That(
                PICOCameraPackSampleConfigurator.ZXingMenuPriority,
                Is.GreaterThan(PICOCameraPackSampleConfigurator.ConfigureDependenciesMenuPriority));
            Assert.That(
                PICOCameraPackSampleConfigurator.UpdateZXingDefineMenuPriority,
                Is.EqualTo(PICOCameraPackSampleConfigurator.ZXingMenuPriority + 1));
        }

        [Test]
        public void DefaultDependencies_IncludeUnitySamplesAndZXing()
        {
            var dependencies = PICOCameraPackSampleConfigurator.GetDefaultDependencies();

            Assert.That(dependencies.Select(dependency => dependency.Id), Is.EquivalentTo(new[]
            {
                "xr-hands-hand-visualizer",
                "xri-starter-assets",
                "zxing-net",
            }));
        }

        [Test]
        public void ZXingDependency_TargetsQRCodeTrackingPluginsFolder()
        {
            var dependency = PICOCameraPackSampleConfigurator.GetDefaultDependencies()
                .Single(item => item.Id == "zxing-net");

            Assert.That(dependency.ImportedPath, Is.EqualTo("Assets/Samples/2 QRCodeTracking/Plugins"));
        }

        [Test]
        public void DefaultDependencies_DescribePurposeForDetailsPanel()
        {
            var dependencies = PICOCameraPackSampleConfigurator.GetDefaultDependencies();

            Assert.That(dependencies.All(dependency => !string.IsNullOrWhiteSpace(dependency.Purpose)), Is.True);
        }

        [Test]
        public void GetDependencyStatuses_ReportsSourcePurposeTargetAndInstallState()
        {
            var dependencies = new[]
            {
                new PICOSampleDependency(
                    "installed",
                    "Installed Dependency",
                    "Assets/Installed",
                    "com.example.package",
                    "Example Sample",
                    "1.0.0",
                    "Used by installed sample."),
                new PICOSampleDependency(
                    "missing",
                    "Missing Dependency",
                    "Assets/Missing",
                    null,
                    null,
                    null,
                    "Used by missing sample."),
            };

            var statuses = PICOCameraPackSampleConfigurator.GetDependencyStatuses(
                dependencies,
                path => path == "Assets/Installed");

            Assert.That(statuses[0].Source, Is.EqualTo("com.example.package@1.0.0 / Example Sample"));
            Assert.That(statuses[0].Status, Is.EqualTo("Installed"));
            Assert.That(statuses[0].Purpose, Is.EqualTo("Used by installed sample."));
            Assert.That(statuses[0].ImportedPath, Is.EqualTo("Assets/Installed"));
            Assert.That(statuses[1].Source, Is.EqualTo("Manual dependency"));
            Assert.That(statuses[1].Status, Is.EqualTo("Missing"));
        }

        [Test]
        public void GetMissingDependencyCount_UsesImportedPathState()
        {
            var dependencies = new[]
            {
                new PICOSampleDependency("installed", "Installed", "Assets/Installed", null, null),
                new PICOSampleDependency("missing", "Missing", "Assets/Missing", null, null),
            };

            int count = PICOCameraPackSampleConfigurator.GetMissingDependencyCount(
                dependencies,
                path => path == "Assets/Installed");

            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void ShouldPromptOnStartup_ReturnsFalseInBatchMode()
        {
            Assert.That(
                PICOCameraPackSampleConfigurator.ShouldPromptOnStartup(
                    promptAlreadyShown: false,
                    missingDependencyCount: 2,
                    isBatchMode: true),
                Is.False);
        }

        [Test]
        public void ApplyZXingDefine_PreservesUnrelatedSymbolsWhenZXingIsMissing()
        {
            string defines = ZXingDefineSymbolChecker.ApplyZXingDefine(
                "ISDK_OPENXR_HAND;SENTIS_ANALYTICS_ENABLED;ENABLE_PICO_XR_SDK",
                hasZXing: false);

            Assert.That(defines, Is.EqualTo("ISDK_OPENXR_HAND;SENTIS_ANALYTICS_ENABLED;ENABLE_PICO_XR_SDK"));
        }
    }
}

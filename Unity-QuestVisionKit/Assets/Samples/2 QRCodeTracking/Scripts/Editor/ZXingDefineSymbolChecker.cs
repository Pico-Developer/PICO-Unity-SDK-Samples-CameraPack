#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using System.IO;
using System.Linq;

public static class ZXingDefineSymbolChecker
{
    [MenuItem(PICO.Editor.PICOCameraPackSampleConfigurator.UpdateZXingDefineMenuPath, false, PICO.Editor.PICOCameraPackSampleConfigurator.UpdateZXingDefineMenuPriority)]
    public static void UpdateZXingDefine()
    {
        var hasZXing = HasZXingDLL();

        var targets = new[]
        {
            NamedBuildTarget.Standalone,
            NamedBuildTarget.Android,
            NamedBuildTarget.iOS,
        };

        foreach (var target in targets)
        {
            try
            {
                var defines = PlayerSettings.GetScriptingDefineSymbols(target);
                var newDefines = ApplyZXingDefine(defines, hasZXing);

                if (newDefines == defines)
                {
                    continue;
                }

                PlayerSettings.SetScriptingDefineSymbols(target, newDefines);
                Debug.Log(hasZXing
                    ? $"[ZXingDefineSymbolChecker] Added ZXING_ENABLED for {target}"
                    : $"[ZXingDefineSymbolChecker] Removed ZXING_ENABLED for {target}");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[ZXingDefineSymbolChecker] Could not update define for target {target}: {ex.Message}");
            }
        }
    }

    public static string ApplyZXingDefine(string defines, bool hasZXing)
    {
        var defineList = defines.Split(';')
                                .Select(s => s.Trim())
                                .Where(s => !string.IsNullOrEmpty(s))
                                .ToList();

        var symbolExists = defineList.Contains("ZXING_ENABLED");

        switch (hasZXing)
        {
            case true when !symbolExists:
                defineList.Add("ZXING_ENABLED");
                break;
            case false when symbolExists:
                defineList.Remove("ZXING_ENABLED");
                break;
        }

        return string.Join(";", defineList);
    }

    private static bool HasZXingDLL()
    {
        var files = Directory.GetFiles(Application.dataPath, "*ZXing.dll", SearchOption.AllDirectories)
                             .Concat(Directory.GetFiles(Application.dataPath, "*zxing.dll", SearchOption.AllDirectories));
        return files.Any();
    }
}
#endif

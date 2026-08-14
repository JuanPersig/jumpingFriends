// Puente para disparar ProfilerCaptureReport desde fuera del Editor (sin
// necesidad de abrir una segunda instancia de Unity ni tocar la UI):
// escribe un archivo "run_report.trigger" con dos líneas (rutaCaptura,
// rutaSalida) en ProfilerCaptures/ y este watcher lo procesa y lo borra.
// Es temporal, solo para diagnóstico; se puede borrar cuando ya no haga falta.
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class ProfilerCaptureReportRunner
{
    static string TriggerFullPath =>
        Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ProfilerCaptures", "run_report.trigger"));

    static string TerrainFixTriggerFullPath =>
        Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ProfilerCaptures", "run_terrain_fix.trigger"));

    static string VerifyTriggerFullPath =>
        Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ProfilerCaptures", "run_verify.trigger"));

    static string InspectMaterialTriggerFullPath =>
        Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ProfilerCaptures", "run_inspect_material.trigger"));

    static string FixMaterialTriggerFullPath =>
        Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ProfilerCaptures", "run_fix_material.trigger"));

    static ProfilerCaptureReportRunner()
    {
        EditorApplication.update += CheckTrigger;
        EditorApplication.update += CheckTerrainFixTrigger;
        EditorApplication.update += CheckVerifyTrigger;
        EditorApplication.update += CheckInspectMaterialTrigger;
        EditorApplication.update += CheckFixMaterialTrigger;
    }

    static void CheckFixMaterialTrigger()
    {
        string path = FixMaterialTriggerFullPath;
        if (!File.Exists(path)) return;

        string prefabPath = null, outPath = null;
        try
        {
            var lines = File.ReadAllLines(path);
            prefabPath = lines.Length > 0 ? lines[0].Trim() : null;
            outPath = lines.Length > 1 ? lines[1].Trim() : null;
        }
        catch (Exception e) { Debug.LogError($"ProfilerCaptureReportRunner: no se pudo leer el trigger de fix material: {e}"); }

        try { File.Delete(path); } catch { }

        if (!string.IsNullOrEmpty(prefabPath) && !string.IsNullOrEmpty(outPath))
        {
            try { TerrainDetailFixer.FixInstancedTerrainMaterial(prefabPath, outPath); }
            catch (Exception e)
            {
                Debug.LogError($"ProfilerCaptureReportRunner: fallo al aplicar fix de material: {e}");
                try { File.WriteAllText(outPath, "ERROR: " + e); } catch { }
            }
        }
    }

    static void CheckInspectMaterialTrigger()
    {
        string path = InspectMaterialTriggerFullPath;
        if (!File.Exists(path)) return;

        string prefabPath = null, outPath = null;
        try
        {
            var lines = File.ReadAllLines(path);
            prefabPath = lines.Length > 0 ? lines[0].Trim() : null;
            outPath = lines.Length > 1 ? lines[1].Trim() : null;
        }
        catch (Exception e) { Debug.LogError($"ProfilerCaptureReportRunner: no se pudo leer el trigger de inspect material: {e}"); }

        try { File.Delete(path); } catch { }

        if (!string.IsNullOrEmpty(prefabPath) && !string.IsNullOrEmpty(outPath))
        {
            try { TerrainDetailFixer.InspectTerrainMaterial(prefabPath, outPath); }
            catch (Exception e)
            {
                Debug.LogError($"ProfilerCaptureReportRunner: fallo al inspeccionar material: {e}");
                try { File.WriteAllText(outPath, "ERROR: " + e); } catch { }
            }
        }
    }

    static void CheckVerifyTrigger()
    {
        string path = VerifyTriggerFullPath;
        if (!File.Exists(path)) return;

        string prefabPath = null, outPath = null;
        try
        {
            var lines = File.ReadAllLines(path);
            prefabPath = lines.Length > 0 ? lines[0].Trim() : null;
            outPath = lines.Length > 1 ? lines[1].Trim() : null;
        }
        catch (Exception e) { Debug.LogError($"ProfilerCaptureReportRunner: no se pudo leer el trigger de verify: {e}"); }

        try { File.Delete(path); } catch { }

        if (!string.IsNullOrEmpty(prefabPath) && !string.IsNullOrEmpty(outPath))
        {
            try { TerrainDetailFixer.VerifyChunkPrefab(prefabPath, outPath); }
            catch (Exception e)
            {
                Debug.LogError($"ProfilerCaptureReportRunner: fallo al verificar prefab: {e}");
                try { File.WriteAllText(outPath, "ERROR: " + e); } catch { }
            }
        }
    }

    static void CheckTerrainFixTrigger()
    {
        string path = TerrainFixTriggerFullPath;
        if (!File.Exists(path)) return;

        string terrainDataPath = null, outPath = null;
        try
        {
            var lines = File.ReadAllLines(path);
            terrainDataPath = lines.Length > 0 ? lines[0].Trim() : null;
            outPath = lines.Length > 1 ? lines[1].Trim() : null;
        }
        catch (Exception e)
        {
            Debug.LogError($"ProfilerCaptureReportRunner: no se pudo leer el trigger de terreno: {e}");
        }

        try { File.Delete(path); } catch { }

        if (!string.IsNullOrEmpty(terrainDataPath) && !string.IsNullOrEmpty(outPath))
        {
            try { TerrainDetailFixer.RunFix(terrainDataPath, outPath); }
            catch (Exception e)
            {
                Debug.LogError($"ProfilerCaptureReportRunner: fallo al aplicar fix de terreno: {e}");
                try { File.WriteAllText(outPath, "ERROR: " + e); } catch { }
            }
        }
    }

    static void CheckTrigger()
    {
        string path = TriggerFullPath;
        if (!File.Exists(path)) return;

        string capturePath = null, outPath = null;
        try
        {
            var lines = File.ReadAllLines(path);
            capturePath = lines.Length > 0 ? lines[0].Trim() : null;
            outPath = lines.Length > 1 ? lines[1].Trim() : null;
        }
        catch (Exception e)
        {
            Debug.LogError($"ProfilerCaptureReportRunner: no se pudo leer el trigger: {e}");
        }

        // Borrar primero para no reprocesar si algo falla dentro del export.
        try { File.Delete(path); } catch { }

        if (!string.IsNullOrEmpty(capturePath) && !string.IsNullOrEmpty(outPath))
        {
            try
            {
                ProfilerCaptureReport.ExportReportPublic(capturePath, outPath);
            }
            catch (Exception e)
            {
                Debug.LogError($"ProfilerCaptureReportRunner: fallo al exportar: {e}");
                try { File.WriteAllText(outPath, "ERROR: " + e); } catch { }
            }
        }
    }
}

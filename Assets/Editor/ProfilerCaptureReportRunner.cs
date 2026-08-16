// Puente para disparar ProfilerCaptureReport/TerrainDetailFixer desde fuera
// del Editor (sin necesidad de abrir una segunda instancia de Unity ni
// tocar la UI): escribe un archivo "run_*.trigger" con dos líneas
// (rutaEntrada, rutaSalida) en ProfilerCaptures/ y este watcher lo procesa
// y lo borra. Es temporal, solo para diagnóstico; se puede borrar cuando ya
// no haga falta.
//
// Las 5 acciones (report / terrain fix / verify / inspect material / fix
// material) seguían EXACTAMENTE el mismo patrón -- leer 2 líneas del
// trigger, borrarlo, y despachar a un método -- repetido letra por letra
// 5 veces. Quedó en una sola tabla (_triggers) + un solo Check() genérico,
// para no seguir copiando el mismo bloque cada vez que se suma una acción
// nueva.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class ProfilerCaptureReportRunner
{
    // (nombre del archivo trigger, acción a ejecutar con (líneaEntrada, líneaSalida)).
    private static readonly (string fileName, Action<string, string> action)[] _triggers =
    {
        ("run_report.trigger", (inPath, outPath) => ProfilerCaptureReport.ExportReportPublic(inPath, outPath)),
        ("run_terrain_fix.trigger", (inPath, outPath) => TerrainDetailFixer.RunFix(inPath, outPath)),
        ("run_verify.trigger", (inPath, outPath) => TerrainDetailFixer.VerifyChunkPrefab(inPath, outPath)),
        ("run_inspect_material.trigger", (inPath, outPath) => TerrainDetailFixer.InspectTerrainMaterial(inPath, outPath)),
        ("run_fix_material.trigger", (inPath, outPath) => TerrainDetailFixer.FixInstancedTerrainMaterial(inPath, outPath)),
        ("run_inspect_detail.trigger", (inPath, outPath) => TerrainDetailFixer.InspectDetailScatter(inPath, outPath)),
        ("run_revert_detail_scatter.trigger", (inPath, outPath) => TerrainDetailFixer.RevertDetailScatterToInstanceCount(inPath, outPath)),
        ("run_inspect_chunks.trigger", (inPath, outPath) => TerrainDetailFixer.InspectLiveChunks(inPath, outPath)),
    };

    static ProfilerCaptureReportRunner()
    {
        foreach ((string fileName, Action<string, string> action) in _triggers)
        {
            string fullPath = TriggerFullPath(fileName);
            EditorApplication.update += () => Check(fullPath, action);
        }
    }

    private static string TriggerFullPath(string fileName) =>
        Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ProfilerCaptures", fileName));

    private static void Check(string path, Action<string, string> action)
    {
        if (!File.Exists(path)) return;

        string inPath = null, outPath = null;
        try
        {
            var lines = File.ReadAllLines(path);
            inPath = lines.Length > 0 ? lines[0].Trim() : null;
            outPath = lines.Length > 1 ? lines[1].Trim() : null;
        }
        catch (Exception e)
        {
            Debug.LogError($"ProfilerCaptureReportRunner: no se pudo leer el trigger '{path}': {e}");
        }

        // Borrar primero para no reprocesar si algo falla dentro de la acción.
        try { File.Delete(path); } catch { }

        if (string.IsNullOrEmpty(inPath) || string.IsNullOrEmpty(outPath)) return;

        try
        {
            action(inPath, outPath);
        }
        catch (Exception e)
        {
            Debug.LogError($"ProfilerCaptureReportRunner: fallo al ejecutar el trigger '{path}': {e}");
            try { File.WriteAllText(outPath, "ERROR: " + e); } catch { }
        }
    }
}

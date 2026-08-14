// Herramienta de diagnóstico temporal: carga una captura .data del Profiler
// y vuelca un reporte de texto con los contadores clave por frame.
//
// Usa reflection para llamar a UnityEditorInternal.ProfilerDriver porque esa
// clase (y el enum ProfilerArea) son "internal" en esta versión de Unity y no
// se pueden referenciar directamente desde un ensamblado de Editor de usuario.
//
// Uso manual: menú Tools > Profiler > Export Capture Report...
// Uso disparado por archivo: ver ProfilerCaptureReportRunner.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

public static class ProfilerCaptureReport
{
    [MenuItem("Tools/Profiler/Export Capture Report...")]
    public static void ExportReportMenu()
    {
        string path = EditorUtility.OpenFilePanel("Selecciona captura .data", "", "data");
        if (string.IsNullOrEmpty(path)) return;
        string outPath = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "_report.txt");
        ExportReport(path, outPath);
        EditorUtility.RevealInFinder(outPath);
    }

    public static void ExportReportPublic(string capturePath, string outPath) => ExportReport(capturePath, outPath);

    // Esta versión del Profiler no expone un contador único "Frame Time":
    // el tiempo total de CPU/GPU es la SUMA de las subcategorías del gráfico
    // apilado (ver CpuBreakdown/GpuBreakdown más abajo).
    static readonly (string area, string name, string label)[] Counters = new (string, string, string)[]
    {
        ("Rendering", "Draw Calls Count", "Draw Calls"),
        ("Rendering", "Batches Count", "Batches"),
        ("Rendering", "SetPass Calls Count", "SetPass Calls"),
        ("Rendering", "Triangles Count", "Triangles"),
        ("Rendering", "Vertices Count", "Vertices"),
        ("Memory", "GC Allocated In Frame", "GC Alloc/Frame"),
        ("Memory", "GC Used Memory", "GC Used Memory"),
        ("Memory", "Total Used Memory", "Total Used Memory"),
        ("Memory", "Texture Memory", "Texture Memory"),
        ("Memory", "Mesh Memory", "Mesh Memory"),
        ("Memory", "Object Count", "Object Count"),
        ("Physics", "Rigidbody", "Rigidbodies"),
        ("Physics", "Active Dynamic", "Active Dynamic Bodies"),
        ("Physics", "Contacts", "Physics Contacts"),
        ("Audio", "Playing Audio Sources", "Audio Playing Sources"),
    };

    static readonly string[] CpuBreakdown = { "Rendering", "Scripts", "Physics", "Animation", "GarbageCollector", "VSync", "Global Illumination", "UI", "Others" };
    static readonly string[] GpuBreakdown = { "Opaque", "Transparent", "Shadows/Depth", "Deferred Geometry", "Deferred Lighting", "PostProcess", "Other" };

    static void ExportReport(string capturePath, string outPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Captura: {capturePath}");

        // Localizar UnityEditorInternal.ProfilerDriver por reflection (es internal).
        Assembly editorAsm = typeof(EditorWindow).Assembly;
        Type profilerDriverType = editorAsm.GetType("UnityEditorInternal.ProfilerDriver");
        Type profilerAreaType = editorAsm.GetType("UnityEditorInternal.ProfilerArea");
        if (profilerDriverType == null)
        {
            sb.AppendLine("ERROR: no se encontró el tipo UnityEditorInternal.ProfilerDriver en esta versión de Unity.");
            File.WriteAllText(outPath, sb.ToString());
            return;
        }

        const BindingFlags SFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        MethodInfo loadProfile = profilerDriverType.GetMethod("LoadProfile", SFlags, null, new[] { typeof(string), typeof(bool) }, null);
        PropertyInfo firstFrameProp = profilerDriverType.GetProperty("firstFrameIndex", SFlags);
        PropertyInfo lastFrameProp = profilerDriverType.GetProperty("lastFrameIndex", SFlags);

        if (loadProfile == null || firstFrameProp == null || lastFrameProp == null)
        {
            sb.AppendLine($"ERROR: API esperada no encontrada. loadProfile={loadProfile != null} firstFrameProp={firstFrameProp != null} lastFrameProp={lastFrameProp != null}");
            sb.AppendLine("Miembros estáticos disponibles en ProfilerDriver:");
            foreach (var m in profilerDriverType.GetMembers(SFlags))
                sb.AppendLine("  " + m.MemberType + " " + m);
            File.WriteAllText(outPath, sb.ToString());
            return;
        }

        bool loaded;
        try
        {
            loaded = (bool)loadProfile.Invoke(null, new object[] { capturePath, false });
        }
        catch (Exception e)
        {
            sb.AppendLine($"ERROR al cargar la captura: {e}");
            File.WriteAllText(outPath, sb.ToString());
            return;
        }

        sb.AppendLine($"Cargada correctamente: {loaded}");
        int first = (int)firstFrameProp.GetValue(null);
        int last = (int)lastFrameProp.GetValue(null);
        sb.AppendLine($"Rango de frames: {first} .. {last}  (total {Math.Max(0, last - first + 1)} frames)");
        sb.AppendLine();

        // El enum ProfilerArea real usado por esta sobrecarga es público: UnityEngine.Profiling.ProfilerArea.
        profilerAreaType = typeof(UnityEngine.Profiling.ProfilerArea);
        MethodInfo getFormatted = profilerDriverType.GetMethod("GetFormattedCounterValue", SFlags, null, new[] { typeof(int), profilerAreaType, typeof(string) }, null);

        if (getFormatted == null || profilerAreaType == null)
        {
            sb.AppendLine("ERROR: no se encontró ProfilerDriver.GetFormattedCounterValue(int, ProfilerArea, string) o el enum ProfilerArea.");
            sb.AppendLine("Métodos 'GetFormattedCounterValue*' disponibles:");
            foreach (var m in profilerDriverType.GetMethods(SFlags).Where(m => m.Name.Contains("GetFormattedCounterValue") || m.Name.Contains("GetCounterValue")))
                sb.AppendLine("  " + m);
            File.WriteAllText(outPath, sb.ToString());
            return;
        }

        // Mapear nombres de área ("CPU", "Rendering", ...) a valores del enum real.
        var areaByName = new Dictionary<string, object>();
        foreach (var v in Enum.GetValues(profilerAreaType))
            areaByName[v.ToString()] = v;

        // Diagnóstico: listar los nombres reales de contadores disponibles por área,
        // para no adivinar mal los nombres (p.ej. "Frame Time" vs "CPU Frame Time").
        MethodInfo getStatNames = profilerDriverType.GetMethod("GetGraphStatisticsPropertiesForArea", SFlags, null, new[] { profilerAreaType }, null);
        if (getStatNames != null)
        {
            sb.AppendLine("== Nombres de contadores disponibles por área (diagnóstico) ==");
            foreach (var kv in areaByName)
            {
                try
                {
                    var namesObj = getStatNames.Invoke(null, new object[] { kv.Value });
                    if (namesObj is System.Collections.IEnumerable en)
                    {
                        var names = en.Cast<object>().Select(o => o?.ToString()).ToArray();
                        sb.AppendLine($"  {kv.Key}: {string.Join(" | ", names)}");
                    }
                }
                catch (Exception e) { sb.AppendLine($"  {kv.Key}: ERROR {e.Message}"); }
            }
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("(no se encontró GetGraphStatisticsPropertiesForArea para listar contadores disponibles)");
        }

        var numericRegex = new Regex(@"[-+]?[0-9]*\.?[0-9]+");
        var series = new Dictionary<string, List<float>>();

        float ReadCounter(int frame, object areaVal, string name)
        {
            try
            {
                string formatted = (string)getFormatted.Invoke(null, new object[] { frame, areaVal, name });
                if (!string.IsNullOrEmpty(formatted))
                {
                    var m = numericRegex.Match(formatted.Replace(",", ""));
                    if (m.Success) return float.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch { /* contador no disponible en esta captura */ }
            return float.NaN;
        }

        // CPU y GPU: sumar las subcategorías del gráfico apilado para obtener el total por frame,
        // y guardar cada subcategoría por separado para saber qué está costando más.
        void SampleBreakdown(string areaName, string[] subNames, string totalLabel, string prefix)
        {
            if (!areaByName.ContainsKey(areaName)) return;
            object areaVal = areaByName[areaName];
            var subSeries = subNames.ToDictionary(n => n, n => new List<float>(Math.Max(0, last - first + 1)));
            var total = new List<float>(Math.Max(0, last - first + 1));
            for (int f = first; f <= last; f++)
            {
                float sum = 0f; bool any = false;
                foreach (var n in subNames)
                {
                    float v = ReadCounter(f, areaVal, n);
                    subSeries[n].Add(v);
                    if (!float.IsNaN(v)) { sum += v; any = true; }
                }
                total.Add(any ? sum : float.NaN);
            }
            series[totalLabel] = total;
            foreach (var n in subNames)
                series[$"{prefix}: {n}"] = subSeries[n];
        }

        SampleBreakdown("CPU", CpuBreakdown, "CPU Frame Time", "CPU");
        SampleBreakdown("GPU", GpuBreakdown, "GPU Frame Time", "GPU");

        var activeCounters = new List<(string area, string name, string label)>();
        foreach (var c in Counters)
        {
            if (!areaByName.ContainsKey(c.area))
            {
                sb.AppendLine($"(aviso: área '{c.area}' no existe en este ProfilerArea, se omite {c.label})");
                continue;
            }
            activeCounters.Add(c);
            series[c.label] = new List<float>(Math.Max(0, last - first + 1));
        }

        for (int f = first; f <= last; f++)
        {
            foreach (var c in activeCounters)
            {
                object areaVal = areaByName[c.area];
                series[c.label].Add(ReadCounter(f, areaVal, c.name));
            }
        }

        void PrintSummaryLine(string label)
        {
            if (!series.TryGetValue(label, out var all)) return;
            var valid = all.Where(v => !float.IsNaN(v)).ToList();
            if (valid.Count == 0) { sb.AppendLine($"{label}: SIN DATOS"); return; }
            valid.Sort();
            float min = valid.First(), max = valid.Last(), avg = valid.Average();
            float p95 = valid[Math.Min(valid.Count - 1, (int)(valid.Count * 0.95f))];
            sb.AppendLine($"{label}: avg={avg:F3}  min={min:F3}  max={max:F3}  p95={p95:F3}  (muestras={valid.Count}/{all.Count})");
        }

        sb.AppendLine("== CPU: tiempo total por frame (ms) y desglose por subcategoría ==");
        PrintSummaryLine("CPU Frame Time");
        foreach (var n in CpuBreakdown) PrintSummaryLine($"CPU: {n}");
        sb.AppendLine();

        sb.AppendLine("== GPU: tiempo total por frame (ms) y desglose por subcategoría ==");
        PrintSummaryLine("GPU Frame Time");
        foreach (var n in GpuBreakdown) PrintSummaryLine($"GPU: {n}");
        sb.AppendLine();

        sb.AppendLine("== Resumen por contador ==");
        foreach (var c in activeCounters) PrintSummaryLine(c.label);
        sb.AppendLine();

        void WorstFrames(string label, int topN)
        {
            if (!series.TryGetValue(label, out var vals)) return;
            var indexed = Enumerable.Range(0, vals.Count)
                .Where(i => !float.IsNaN(vals[i]))
                .Select(i => (frame: first + i, val: vals[i]))
                .OrderByDescending(x => x.val)
                .Take(topN)
                .ToList();
            if (indexed.Count == 0) return;
            sb.AppendLine($"== Peores {topN} frames por {label} ==");
            foreach (var it in indexed)
            {
                string extra = label == "CPU Frame Time" && it.val > 0 ? $"  (~{1000f / it.val:F1} fps)" : "";
                sb.AppendLine($"  frame {it.frame}: {it.val:F3}{extra}");
            }
            sb.AppendLine();
        }

        WorstFrames("CPU Frame Time", 30);
        WorstFrames("GPU Frame Time", 15);
        WorstFrames("GC Alloc/Frame", 30);
        WorstFrames("Draw Calls", 15);
        WorstFrames("SetPass Calls", 15);

        if (series.TryGetValue("CPU Frame Time", out var cpuFrames))
        {
            var valid = cpuFrames.Where(v => !float.IsNaN(v) && v > 0).ToList();
            if (valid.Count > 0)
            {
                int b16 = valid.Count(v => v <= 16.7f);
                int b33 = valid.Count(v => v > 16.7f && v <= 33.4f);
                int b50 = valid.Count(v => v > 33.4f && v <= 50f);
                int bBad = valid.Count(v => v > 50f);
                sb.AppendLine("== Distribución de frames por rango de FPS (según CPU Frame Time) ==");
                sb.AppendLine($"  >=60 fps (<=16.7ms): {b16} frames ({100f * b16 / valid.Count:F1}%)");
                sb.AppendLine($"  30-60 fps: {b33} frames ({100f * b33 / valid.Count:F1}%)");
                sb.AppendLine($"  20-30 fps: {b50} frames ({100f * b50 / valid.Count:F1}%)");
                sb.AppendLine($"  <20 fps: {bBad} frames ({100f * bBad / valid.Count:F1}%)");
            }
        }

        // Top markers (métodos concretos) en los peores frames, para identificar la causa real.
        try
        {
            var worstCpuFrames = series.TryGetValue("CPU Frame Time", out var cpuVals)
                ? Enumerable.Range(0, cpuVals.Count)
                    .Where(i => !float.IsNaN(cpuVals[i]))
                    .Select(i => (frame: first + i, val: cpuVals[i]))
                    .OrderByDescending(x => x.val)
                    .Take(8)
                    .Select(x => x.frame)
                    .ToList()
                : new List<int>();

            sb.AppendLine("== Diagnóstico: métodos que más contribuyen en los peores frames ==");
            var ghdvMethods = profilerDriverType.GetMethods(SFlags).Where(m => m.Name.Contains("HierarchyFrameDataView")).ToList();
            sb.AppendLine("(métodos GetHierarchyFrameDataView disponibles: " + string.Join(" ; ", ghdvMethods.Select(m => m.ToString())) + ")");

            MethodInfo ghdv = ghdvMethods.FirstOrDefault(m => m.GetParameters().Length == 5);
            if (ghdv != null)
            {
                Type viewModesType = typeof(UnityEditor.Profiling.HierarchyFrameDataView).GetNestedType("ViewModes");
                object mergeMode = viewModesType != null ? Enum.Parse(viewModesType, "MergeSamplesWithTheSameName") : null;
                int colSelfTime = (int)typeof(UnityEditor.Profiling.HierarchyFrameDataView).GetField("columnSelfTime").GetValue(null);
                int colTotalTime = (int)typeof(UnityEditor.Profiling.HierarchyFrameDataView).GetField("columnTotalTime").GetValue(null);

                foreach (var frame in worstCpuFrames)
                {
                    object viewObj;
                    try { viewObj = ghdv.Invoke(null, new object[] { frame, 0, mergeMode, colSelfTime, false }); }
                    catch (Exception e) { sb.AppendLine($"  frame {frame}: ERROR obteniendo HierarchyFrameDataView: {e.InnerException?.Message ?? e.Message}"); continue; }

                    var view = viewObj as UnityEditor.Profiling.HierarchyFrameDataView;
                    if (view == null || !view.valid) { sb.AppendLine($"  frame {frame}: vista no válida"); continue; }

                    // Recorrer todo el árbol del hilo principal y sumar self-time por nombre de método,
                    // para encontrar el/los métodos concretos que realmente consumen el tiempo (no solo
                    // el nodo raíz "PlayerLoop", que agrega todo).
                    var selfByName = new Dictionary<string, float>();
                    var callCountByName = new Dictionary<string, int>();
                    var stack = new Stack<int>();
                    stack.Push(view.GetRootItemID());
                    var childBuf = new List<int>();
                    int visited = 0;
                    while (stack.Count > 0 && visited < 200000)
                    {
                        int id = stack.Pop();
                        visited++;
                        string name = view.GetItemName(id);
                        float self = view.GetItemColumnDataAsSingle(id, colSelfTime);
                        if (self > 0f)
                        {
                            selfByName.TryGetValue(name, out float acc);
                            selfByName[name] = acc + self;
                            callCountByName.TryGetValue(name, out int cnt);
                            callCountByName[name] = cnt + 1;
                        }
                        childBuf.Clear();
                        view.GetItemChildren(id, childBuf);
                        foreach (var c in childBuf) stack.Push(c);
                    }

                    var top = selfByName.OrderByDescending(kv => kv.Value).Take(10).ToList();
                    sb.AppendLine($"  frame {frame} (top métodos por self-time acumulado en el hilo principal, ms):");
                    foreach (var t in top)
                        sb.AppendLine($"    {t.Key}: self={t.Value:F3}ms  (apariciones={callCountByName[t.Key]})");
                }
            }
            else
            {
                sb.AppendLine("  (no se encontró una sobrecarga de GetHierarchyFrameDataView con 5 parámetros)");
            }
        }
        catch (Exception e)
        {
            sb.AppendLine($"(diagnóstico de métodos falló: {e})");
        }

        File.WriteAllText(outPath, sb.ToString());
        Debug.Log($"Reporte del Profiler escrito en: {outPath}");
    }
}

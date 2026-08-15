// Diagnóstico + fix puntual: el modo de renderizado del detail/grass de un
// Terrain puede ser "Legacy" (VertexLit, un draw call por parche, caro en
// CPU/GPU) o "Instanced" (GPU instancing, mucho más barato). Esto lo
// inspecciona y, si hace falta, lo cambia a instanciado.
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class TerrainDetailFixer
{
    [MenuItem("Tools/Profiler/Inspect + Fix Terrain Detail Mode...")]
    public static void RunMenu()
    {
        string path = EditorUtility.OpenFilePanel("Selecciona el TerrainData", "Assets", "asset");
        if (string.IsNullOrEmpty(path)) return;
        // Convertir a ruta relativa "Assets/..." para AssetDatabase.
        string projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string rel = Path.GetFullPath(path).Substring(projectPath.Length + 1).Replace('\\', '/');
        string outPath = Path.Combine(Path.GetDirectoryName(path), "terrain_detail_report.txt");
        RunFix(rel, outPath);
        EditorUtility.RevealInFinder(outPath);
    }

    // Fuerza un AssetDatabase.Refresh() y confirma qué valor tiene realmente
    // cargado el Terrain de un prefab -- por si el auto-refresh de Unity
    // todavía no agarró un cambio hecho por fuera del Editor.
    public static void VerifyChunkPrefab(string prefabAssetPath, string outPath)
    {
        var sb = new System.Text.StringBuilder();
        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

        GameObject root = null;
        try
        {
            root = PrefabUtility.LoadPrefabContents(prefabAssetPath);
            var terrain = root != null ? root.GetComponentInChildren<Terrain>(true) : null;
            if (terrain == null)
            {
                sb.AppendLine($"ERROR: no se encontró un Terrain en '{prefabAssetPath}'");
            }
            else
            {
                sb.AppendLine($"Prefab: {prefabAssetPath}");
                sb.AppendLine($"Terrain.drawInstanced = {terrain.drawInstanced}");
                sb.AppendLine($"Terrain.treeDistance = {terrain.treeDistance}");
                sb.AppendLine($"Terrain.detailObjectDistance = {terrain.detailObjectDistance}");
                sb.AppendLine($"Terrain.detailObjectDensity = {terrain.detailObjectDensity}");
            }
        }
        finally
        {
            if (root != null) PrefabUtility.UnloadPrefabContents(root);
        }

        File.WriteAllText(Path.GetFullPath(outPath), sb.ToString());
        Debug.Log($"[TerrainDetailFixer] Verificación escrita en: {outPath}");
    }

    // Investiga por qué Terrain.drawInstanced=true deja el suelo invisible
    // en build standalone aunque en el Editor se vea bien: mira el material
    // template del Terrain, sus keywords/shader, y si tiene GPU Instancing
    // habilitado -- el sospechoso más probable es shader stripping (HDRP
    // no incluye en el build la variante instanciada del shader del Terrain
    // porque el prefab no está colocado en ninguna escena, solo referenciado
    // desde un array del Inspector).
    public static void InspectTerrainMaterial(string prefabAssetPath, string outPath)
    {
        var sb = new System.Text.StringBuilder();
        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

        GameObject root = null;
        try
        {
            root = PrefabUtility.LoadPrefabContents(prefabAssetPath);
            var terrain = root != null ? root.GetComponentInChildren<Terrain>(true) : null;
            if (terrain == null)
            {
                sb.AppendLine($"ERROR: no se encontró Terrain en '{prefabAssetPath}'");
                File.WriteAllText(Path.GetFullPath(outPath), sb.ToString());
                return;
            }

            Material mat = terrain.materialTemplate;
            sb.AppendLine($"Terrain.drawInstanced = {terrain.drawInstanced}");
            if (mat == null)
            {
                sb.AppendLine("materialTemplate es null.");
            }
            else
            {
                string matPath = AssetDatabase.GetAssetPath(mat);
                sb.AppendLine($"materialTemplate: {mat.name}");
                sb.AppendLine($"  asset path: {matPath}");
                sb.AppendLine($"  shader: {(mat.shader != null ? mat.shader.name : "null")}");
                sb.AppendLine($"  shader path: {(mat.shader != null ? AssetDatabase.GetAssetPath(mat.shader) : "-")}");
                sb.AppendLine($"  enableInstancing: {mat.enableInstancing}");
                sb.AppendLine($"  shaderKeywords: {string.Join(" | ", mat.shaderKeywords)}");
                if (mat.shader != null)
                {
                    int passCount = mat.passCount;
                    sb.AppendLine($"  passCount: {passCount}");
                }
            }

            // TerrainData también expone un flag de instancing propio para el heightmap.
            TerrainData data = terrain.terrainData;
            if (data != null)
            {
                sb.AppendLine($"TerrainData: {AssetDatabase.GetAssetPath(data)}");
            }

            // Estado de los graphics tiers / shader stripping relevantes.
            sb.AppendLine($"GraphicsSettings.currentRenderPipeline: {(UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null ? UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline.name : "null (Built-in)")}");
        }
        finally
        {
            if (root != null) PrefabUtility.UnloadPrefabContents(root);
        }

        File.WriteAllText(Path.GetFullPath(outPath), sb.ToString());
        Debug.Log($"[TerrainDetailFixer] Inspección de material escrita en: {outPath}");
    }

    [MenuItem("Tools/Profiler/Fix Instanced Terrain Material (Chunk_A)")]
    public static void FixInstancedTerrainMaterialMenu()
    {
        string outPath = "ProfilerCaptures/terrain_material_fix.txt";
        FixInstancedTerrainMaterial("Assets/Chunk/Chunk_A.prefab", outPath);
        EditorUtility.RevealInFinder(outPath);
    }

    // Fix real: el materialTemplate del Terrain (TerrainLit.mat, del paquete
    // de URP) tenía "Enable GPU Instancing" apagado mientras el componente
    // Terrain pedía dibujar instanciado -- ese desajuste es la causa más
    // probable de que el suelo desapareciera en build. No se puede editar
    // un material que vive adentro de Packages/, así que se hace una copia
    // local en Assets/ con instancing prendido y se la asigna al Terrain.
    public static void FixInstancedTerrainMaterial(string prefabAssetPath, string outPath)
    {
        var sb = new System.Text.StringBuilder();
        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

        GameObject root = null;
        try
        {
            root = PrefabUtility.LoadPrefabContents(prefabAssetPath);
            var terrain = root != null ? root.GetComponentInChildren<Terrain>(true) : null;
            if (terrain == null)
            {
                sb.AppendLine($"ERROR: no se encontró Terrain en '{prefabAssetPath}'");
                File.WriteAllText(Path.GetFullPath(outPath), sb.ToString());
                return;
            }

            Material source = terrain.materialTemplate;
            if (source == null)
            {
                sb.AppendLine("ERROR: materialTemplate es null, no hay nada que copiar.");
                File.WriteAllText(Path.GetFullPath(outPath), sb.ToString());
                return;
            }

            string prefabDir = Path.GetDirectoryName(prefabAssetPath).Replace('\\', '/');
            string destPath = $"{prefabDir}/{source.name}_Instanced.mat";

            Material existing = AssetDatabase.LoadAssetAtPath<Material>(destPath);
            if (existing == null)
            {
                string sourcePath = AssetDatabase.GetAssetPath(source);
                bool copied = AssetDatabase.CopyAsset(sourcePath, destPath);
                sb.AppendLine($"Copiando '{sourcePath}' -> '{destPath}': {(copied ? "OK" : "FALLÓ")}");
                if (!copied)
                {
                    File.WriteAllText(Path.GetFullPath(outPath), sb.ToString());
                    return;
                }
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                existing = AssetDatabase.LoadAssetAtPath<Material>(destPath);
            }
            else
            {
                sb.AppendLine($"Ya existía '{destPath}', reusando.");
            }

            if (existing == null)
            {
                sb.AppendLine($"ERROR: no se pudo cargar el material copiado en '{destPath}'");
                File.WriteAllText(Path.GetFullPath(outPath), sb.ToString());
                return;
            }

            existing.enableInstancing = true;
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssets();
            sb.AppendLine($"'{destPath}'.enableInstancing = {existing.enableInstancing}");

            terrain.materialTemplate = existing;
            terrain.drawInstanced = true;

            PrefabUtility.SaveAsPrefabAsset(root, prefabAssetPath);
            sb.AppendLine($"Terrain.materialTemplate -> {destPath}");
            sb.AppendLine($"Terrain.drawInstanced -> {terrain.drawInstanced}");
            sb.AppendLine("Prefab guardado.");
        }
        finally
        {
            if (root != null) PrefabUtility.UnloadPrefabContents(root);
        }

        File.WriteAllText(Path.GetFullPath(outPath), sb.ToString());
        Debug.Log($"[TerrainDetailFixer] Fix de material instanciado escrito en: {outPath}");
    }

    // Puramente de lectura -- a diferencia de RunFix (más abajo), esto NO
    // toca detailScatterMode ni useInstancing. Se agregó el 14/8 para
    // diagnosticar "el pasto se ve carguadísimo aunque baje la densidad" sin
    // arriesgarse a mutar el TerrainData mientras se investiga la causa.
    public static void InspectDetailScatter(string terrainDataAssetPath, string outPath)
    {
        var sb = new System.Text.StringBuilder();
        var data = AssetDatabase.LoadAssetAtPath<TerrainData>(terrainDataAssetPath);
        if (data == null)
        {
            sb.AppendLine($"ERROR: no se pudo cargar TerrainData en '{terrainDataAssetPath}'");
            File.WriteAllText(Path.GetFullPath(outPath), sb.ToString());
            return;
        }

        sb.AppendLine($"TerrainData: {terrainDataAssetPath}");
        sb.AppendLine($"detailScatterMode: {data.detailScatterMode}");
        sb.AppendLine($"detailResolution: {data.detailResolution}");
        sb.AppendLine($"detailResolutionPerPatch: {data.detailResolutionPerPatch}");
        sb.AppendLine($"wavingGrassStrength: {data.wavingGrassStrength}");
        sb.AppendLine($"detailPrototypes: {data.detailPrototypes.Length}");
        for (int i = 0; i < data.detailPrototypes.Length; i++)
        {
            var p = data.detailPrototypes[i];
            string label = p.prototype != null ? p.prototype.name : (p.prototypeTexture != null ? p.prototypeTexture.name : "(sin asset)");
            sb.AppendLine($"  [{i}] {label}");
            sb.AppendLine($"      renderMode={p.renderMode} usePrototypeMesh={p.usePrototypeMesh} useInstancing={p.useInstancing}");
            sb.AppendLine($"      density={p.density} targetCoverage={p.targetCoverage} useDensityScaling={p.useDensityScaling}");
            sb.AppendLine($"      minWidth={p.minWidth} maxWidth={p.maxWidth} minHeight={p.minHeight} maxHeight={p.maxHeight}");
            sb.AppendLine($"      noiseSpread={p.noiseSpread} alignToGround={p.alignToGround} positionJitter={p.positionJitter}");
        }

        string fullOutPath = Path.GetFullPath(outPath);
        File.WriteAllText(fullOutPath, sb.ToString());
        Debug.Log($"[TerrainDetailFixer] Inspección (solo lectura) escrita en: {fullOutPath}");
    }

    // REVIERTE el "fix" de detailScatterMode de RunFix (más abajo). Se agregó
    // el 14/8 apenas se confirmó en el juego real (no en los datos, EN EL
    // JUEGO) que pasar a CoverageMode dejaba el terreno SIN PASTO -- pasar
    // detailScatterMode de un modo a otro por la API de Unity NO convierte
    // los datos de detalle ya pintados, solo cambia cómo se interpretan los
    // mismos bytes. Este terreno se pintó bajo InstanceCountMode; forzarlo a
    // CoverageMode sin una conversión real (que la API de script no hace)
    // deja esos datos en un formato que esa lectura no puede reconstruir.
    // Volver a InstanceCountMode es la única forma de recuperar el pasto ya
    // pintado sin re-pintar todo el terreno de cero.
    public static void RevertDetailScatterToInstanceCount(string terrainDataAssetPath, string outPath)
    {
        var sb = new System.Text.StringBuilder();
        var data = AssetDatabase.LoadAssetAtPath<TerrainData>(terrainDataAssetPath);
        if (data == null)
        {
            sb.AppendLine($"ERROR: no se pudo cargar TerrainData en '{terrainDataAssetPath}'");
            File.WriteAllText(Path.GetFullPath(outPath), sb.ToString());
            return;
        }

        sb.AppendLine($"TerrainData: {terrainDataAssetPath}");
        sb.AppendLine($"detailScatterMode actual: {data.detailScatterMode}");

        if (data.detailScatterMode != DetailScatterMode.InstanceCountMode)
        {
            sb.AppendLine("-> Revirtiendo a InstanceCountMode (el modo bajo el que este terreno fue pintado de verdad).");
            data.SetDetailScatterMode(DetailScatterMode.InstanceCountMode);
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            sb.AppendLine("Guardado.");
        }
        else
        {
            sb.AppendLine("-> Ya estaba en InstanceCountMode, no hacía falta revertir.");
        }

        File.WriteAllText(Path.GetFullPath(outPath), sb.ToString());
        Debug.Log($"[TerrainDetailFixer] Revert de detailScatterMode escrito en: {outPath}");
    }

    // OJO (14/8): el cambio automático de detailScatterMode que hacía este
    // método (más abajo) resultó ser un error práctico, no solo el bug de
    // nombre que se pensaba -- ver comentario grande en
    // RevertDetailScatterToInstanceCount. Se dejó de tocar detailScatterMode
    // acá: ahora solo reporta el valor actual (sin cambiarlo) y sigue
    // arreglando useInstancing por prototipo, que sí era un fix real y
    // seguro. Si en algún momento hace falta migrar de verdad a
    // CoverageMode, hay que hacerlo desde la ventana de Paint Details del
    // Editor (que sí ofrece convertir los datos pintados), no por script.
    public static void RunFix(string terrainDataAssetPath, string outPath)
    {
        var sb = new System.Text.StringBuilder();
        var data = AssetDatabase.LoadAssetAtPath<TerrainData>(terrainDataAssetPath);
        if (data == null)
        {
            sb.AppendLine($"ERROR: no se pudo cargar TerrainData en '{terrainDataAssetPath}'");
            File.WriteAllText(outPath, sb.ToString());
            return;
        }

        sb.AppendLine($"TerrainData: {terrainDataAssetPath}");
        sb.AppendLine($"detailScatterMode actual: {data.detailScatterMode}");
        sb.AppendLine($"detailPrototypes: {data.detailPrototypes.Length}");
        for (int i = 0; i < data.detailPrototypes.Length; i++)
        {
            var p = data.detailPrototypes[i];
            sb.AppendLine($"  [{i}] renderMode={p.renderMode} usePrototypeMesh={p.usePrototypeMesh} useInstancing={p.useInstancing} density={p.density} targetCoverage={p.targetCoverage}");
        }

        sb.AppendLine("Valores posibles de DetailScatterMode: " + string.Join(", ", System.Enum.GetNames(typeof(DetailScatterMode))));

        // Ya NO se toca detailScatterMode acá -- ver comentario grande en
        // RevertDetailScatterToInstanceCount sobre por qué cambiarlo por
        // script (aunque el nombre del enum pareciera "el correcto") deja el
        // terreno sin pasto: la API no convierte los datos ya pintados,
        // solo cambia cómo se leen los mismos bytes. Esto solo informa el
        // valor actual, no lo cambia.
        bool changed = false;

        for (int i = 0; i < data.detailPrototypes.Length; i++)
        {
            if (!data.detailPrototypes[i].useInstancing)
            {
                var arr = data.detailPrototypes;
                arr[i].useInstancing = true;
                data.detailPrototypes = arr;
                sb.AppendLine($"-> Prototype [{i}] ({arr[i].prototype?.name ?? arr[i].prototypeTexture?.name}): activado useInstancing.");
                changed = true;
            }
        }

        if (changed)
        {
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            sb.AppendLine("Guardado.");
        }

        string fullOutPath = Path.GetFullPath(outPath);
        File.WriteAllText(fullOutPath, sb.ToString());
        bool ok = File.Exists(fullOutPath);
        long len = ok ? new FileInfo(fullOutPath).Length : -1;
        Debug.Log($"[TerrainDetailFixer] Reporte en: {fullOutPath}  (existe={ok}, bytes={len})");

        // Diagnóstico extra: por qué un archivo que Unity confirma que existe
        // no aparece para procesos externos -- listar el directorio tal como
        // lo ve Unity, y volver a chequear un par de segundos después.
        try
        {
            string dir = Path.GetDirectoryName(fullOutPath);
            var filesNow = Directory.GetFiles(dir);
            Debug.Log($"[TerrainDetailFixer] Directory.GetFiles({dir}) ve {filesNow.Length} archivos: {string.Join(" | ", filesNow.Select(Path.GetFileName))}");
        }
        catch (System.Exception e) { Debug.Log($"[TerrainDetailFixer] Directory.GetFiles falló: {e.Message}"); }

        System.Threading.Thread.Sleep(2000);
        bool okAfter = File.Exists(fullOutPath);
        Debug.Log($"[TerrainDetailFixer] Re-chequeo 2s después: existe={okAfter}");
    }
}

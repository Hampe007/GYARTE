#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

public sealed class TileSceneGeneratorWindow : EditorWindow
{
    [SerializeField] private TileSceneGenerator generator;
    private SerializedObject _serializedGenerator;
    private Vector2 _scrollPos;
    private GUIStyle _monoStyle;

    [MenuItem("Tools/Tiles/Tile Scene Generator & Re-slicer")]
    private static void Open() => GetWindow<TileSceneGeneratorWindow>();

    private void OnEnable()
    {
        EnsureGenerator();
    }

    private void OnGUI()
    {
        EnsureGenerator();
        generator.EnsureReadyForUi();

        _serializedGenerator.Update();

        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

        DrawHeaderSection();
        DrawOutputFolderSection();
        DrawSlicingSection();
        DrawCleanupSection();
        DrawAdvancedOptionsSection();

        EditorGUILayout.EndScrollView();

        _serializedGenerator.ApplyModifiedProperties();
    }

    private void DrawHeaderSection()
    {
        if (generator.IsRunning)
            EditorGUILayout.HelpBox("Slicing in progress… controls are disabled to avoid touching live scene objects.", MessageType.Info);
    }

    private void DrawOutputFolderSection()
    {
        DrawSectionHeader("Output Folder Settings","Controls where generated tile scenes, TerrainData assets, and derived content are written under the Assets folder.");
        
        EditorGUILayout.PropertyField(Find("sceneNamePattern"), new GUIContent("Scene Name Pattern", "Naming pattern for generated scenes. Tokens: {t} terrain name, {x} column, {y} row."));
        EditorGUILayout.PropertyField(Find("outputFolder"), new GUIContent("Output Root Folder", "Root folder under Assets where generated tile output will be saved."));
        EditorGUILayout.PropertyField(Find("terrainDataPrefix"), new GUIContent("TerrainData Prefix", "Prefix used when generating TerrainData assets per tile."));
        EditorGUILayout.PropertyField(Find("subfolderPerTerrain"), new GUIContent("Subfolder Per Terrain", "When enabled, each terrain writes into its own output subfolder."));
    }

    private void DrawSlicingSection()
    {
        DrawSectionHeader("Slicing","Select source terrains, configure tile sizing rules, and execute slice or re-slice operations.");

        EditorGUILayout.PropertyField(Find("autoCollectTerrains"), new GUIContent("Auto-collect Terrains", "Use Terrain.activeTerrains and filter by name prefix."));

        using (new EditorGUI.IndentLevelScope())
        {
            EditorGUILayout.PropertyField(Find("terrainNamePrefix"), new GUIContent("Name Prefix", "Terrain name prefix to include when auto-collecting."));
        }

        if (!Find("autoCollectTerrains").boolValue)
        {
            EditorGUILayout.PropertyField(Find("sourceTerrains"), new GUIContent("Source Terrains (Manual)", "Terrains to process when auto-collect is disabled."), true);
        }
        else if (GUILayout.Button(new GUIContent("Auto-fill now", "Scan Terrain.activeTerrains and log the current candidates.")))
        {
            generator.AutoFillAndLogTerrains();
        }

        EditorGUILayout.PropertyField(Find("settings"), new GUIContent("Shared Settings", "TileSliceSettings asset used to persist slicer settings and results."));

        var settingsAssetPaths = generator.FindAllSettingsAssetPaths();
        if (settingsAssetPaths.Length > 1)
        {
            EditorGUILayout.HelpBox(
                $"Multiple TileSliceSettings assets found ({settingsAssetPaths.Length}). The generator always uses the canonical asset at {TileSceneGenerator.CanonicalSettingsAssetPath}.",
                MessageType.Warning);
        }

        if (Find("settings").objectReferenceValue == null && GUILayout.Button(new GUIContent("Create TileSliceSettings asset", $"Create a TileSliceSettings asset at {TileSceneGenerator.CanonicalSettingsAssetPath}.")))
        {
            generator.EnsureReadyForUi();
            _serializedGenerator.Update();

            var settingsAsset = Find("settings").objectReferenceValue;
            if (settingsAsset != null)
                EditorGUIUtility.PingObject(settingsAsset);
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.PropertyField(Find("tileSizeMeters"), new GUIContent("Tile Size (meters)", "Desired tile size in meters before optional even-fit adjustment."));
        EditorGUILayout.PropertyField(Find("evenFitNoRemainder"), new GUIContent("Even Fit (no remainder)", "Adjust tile dimensions so terrain divides evenly into whole tiles."));
        using (new EditorGUI.IndentLevelScope())
        {
            EditorGUILayout.PropertyField(Find("forceSquareTiles"), new GUIContent("Force Square Tiles", "When even-fit is on, force equal X and Z tile size."));
        }

        DrawSectionHeader("Data Channels","Controls which terrain data layers are copied into generated tiles.");
        
        EditorGUILayout.PropertyField(Find("copyHeights"), new GUIContent("Copy Heights", "Copy terrain heights into generated tiles."));
        EditorGUILayout.PropertyField(Find("copyAlphamaps"), new GUIContent("Copy Splatmaps", "Copy terrain texture splatmaps into generated tiles."));
        EditorGUILayout.PropertyField(Find("copyDetails"), new GUIContent("Copy Details", "Copy detail layers such as grass into generated tiles."));
        EditorGUILayout.PropertyField(Find("copyTrees"), new GUIContent("Copy Trees", "Copy tree instances into generated tiles."));
        EditorGUILayout.PropertyField(Find("copyProps"), new GUIContent("Copy Props", "Copy TileProp-based props and generate deterministic prop data assets."));

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(new GUIContent("Preset: Fast", "Enable heights only for quick validation runs."), GUILayout.Width(160f)))
            {
                Find("copyHeights").boolValue = true;
                Find("copyAlphamaps").boolValue = false;
                Find("copyDetails").boolValue = false;
                Find("copyTrees").boolValue = false;
                Find("copyProps").boolValue = false;
            }

            if (GUILayout.Button(new GUIContent("Preset: Full Fidelity", "Enable all copy channels for final output."), GUILayout.Width(180f)))
            {
                Find("copyHeights").boolValue = true;
                Find("copyAlphamaps").boolValue = true;
                Find("copyDetails").boolValue = true;
                Find("copyTrees").boolValue = true;
                Find("copyProps").boolValue = true;
            }
        }

        DrawSectionHeader("Execution Preview","Preview the slice result and execute the operation.");
        
        string preview = generator.BuildPreviewText();
        if (!string.IsNullOrEmpty(preview))
        {
            if (preview.StartsWith("[Preview error]", System.StringComparison.Ordinal))
            {
                EditorGUILayout.HelpBox(preview, MessageType.Warning);
            }
            
            EditorGUILayout.BeginVertical("box");
            foreach (string line in preview.Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    EditorGUILayout.LabelField(line, MonoStyle);
            }
            EditorGUILayout.EndVertical();
        }
        else
        {
            EditorGUILayout.HelpBox("No terrains found for preview. Check auto-collect prefix or assign terrains manually.", MessageType.Warning);
        }

        using (new EditorGUI.DisabledScope(!generator.CanRun()))
        {
            if (GUILayout.Button(new GUIContent("Run Slice / Re-slice", "Generate tile scenes or update existing tile terrain data.")))
                generator.RunSliceWithDialogs();
        }
    }

    private void DrawCleanupSection()
    {
        DrawSectionHeader("Cleanup","Inspect or remove previously generated tile scenes and derived assets.");
        
        using (new EditorGUI.DisabledScope(generator.IsRunning))
        {
            if (GUILayout.Button(new GUIContent("Reveal Output Folder", "Open the generated output folder in the system file browser.")))
                generator.RevealOutputFolder();

            if (GUILayout.Button(new GUIContent("Delete Generated Tile Scenes", "Delete generated tile scene files from output folders.")))
                generator.DeleteGeneratedScenesWithDialog();

            if (GUILayout.Button(new GUIContent("Delete Generated TerrainData/Assets", "Delete generated TerrainData and related output assets.")))
                generator.DeleteGeneratedAssetsWithDialog();

            if (GUILayout.Button(new GUIContent("Delete ALL Generated Tile Output", "Delete all generated scene and asset output for current settings.")))
                generator.DeleteAllGeneratedOutputWithDialog();
        }
    }

    private void DrawAdvancedOptionsSection()
    {
        DrawSectionHeader("Advanced Options","Control reslice behavior, build settings integration, and optimization rules.");
        
        bool previousWideMode = EditorGUIUtility.wideMode;
        float previousLabelWidth = EditorGUIUtility.labelWidth;
        try
        {
            EditorGUIUtility.wideMode = false;
            EditorGUIUtility.labelWidth = 230f;

            EditorGUILayout.PropertyField(Find("nonDestructiveReslice"), new GUIContent("Non-Destructive Re-slice", "Update tile terrain data in place while preserving other scene content."));
            EditorGUILayout.PropertyField(Find("onlyUpdateIfChanged"), new GUIContent("Only Update If Changed (heights)", "Skip tiles whose heightmap is unchanged for faster reslices."));
            EditorGUILayout.PropertyField(Find("addToBuildSettings"), new GUIContent("Ensure In Build Settings", "Add generated tile scenes to Build Settings automatically."));
            EditorGUILayout.PropertyField(Find("maxTilesPerAxis"), new GUIContent("Max Tiles Per Axis", "Safety cap to prevent accidental generation of extreme tile counts that can freeze or crash the Unity Editor."));
            EditorGUILayout.PropertyField(Find("maxTilesPerTerrain"), new GUIContent("Max Tiles Per Terrain", "Safety cap for total tiles per terrain. Prevents runaway slice jobs when tile size is set too small."));
            EditorGUILayout.PropertyField(Find("clearConsoleBeforeActions"), new GUIContent("Clear Console Before Actions", "Optional: clear Unity Console at action start. Disable to preserve multi-step debugging history."));
        }
        finally
        {
            EditorGUIUtility.wideMode = previousWideMode;
            EditorGUIUtility.labelWidth = previousLabelWidth;
        }
        
        if (GUILayout.Button(new GUIContent("Clean Build Settings", "Remove missing scene entries from Build Settings.")))
            generator.CleanBuildSettingsWithDialog();
    }
    
    private void DrawSectionHeader(string title, string tooltip = null)
    {
        EditorGUILayout.Space(12);

        Rect rect = EditorGUILayout.GetControlRect(false, 22f);
        EditorGUI.DrawRect(
            new Rect(rect.x, rect.y + 18f, rect.width, 1f),
            new Color(0.25f, 0.25f, 0.25f)
        );

        EditorGUI.LabelField(
            rect,
            new GUIContent(title, tooltip),
            EditorStyles.boldLabel
        );
    }

    private SerializedProperty Find(string propertyName) => _serializedGenerator.FindProperty(propertyName);

    private GUIStyle MonoStyle
    {
        get
        {
            if (_monoStyle != null)
                return _monoStyle;

            _monoStyle = new GUIStyle(EditorStyles.label)
            {
                font = EditorGUIUtility.Load("Fonts/RobotoMono/RobotoMono-Regular.ttf") as Font,
                wordWrap = false,
                fontSize = 11
            };

            if (_monoStyle.font == null)
                _monoStyle.font = Font.CreateDynamicFontFromOSFont("Consolas", 11);

            return _monoStyle;
        }
    }

    private const string GeneratorAssetPath = "Assets/Code/Scripts/Tiles/Editor/TileSceneGeneratorWindowState.asset";

    private void EnsureGenerator()
    {
        if (generator == null)
        {
            generator = AssetDatabase.LoadAssetAtPath<TileSceneGenerator>(GeneratorAssetPath);

            if (generator == null)
            {
                EnsureFolderExists("Assets/Editor");
                generator = CreateInstance<TileSceneGenerator>();
                AssetDatabase.CreateAsset(generator, GeneratorAssetPath);
                AssetDatabase.SaveAssets();
            }
        }

        if (_serializedGenerator == null || _serializedGenerator.targetObject != generator)
            _serializedGenerator = new SerializedObject(generator);
    }

    private static void EnsureFolderExists(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;

        string parent = System.IO.Path.GetDirectoryName(path)?.Replace("\\", "/");
        string leaf = System.IO.Path.GetFileName(path);

        if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            EnsureFolderExists(parent);

        AssetDatabase.CreateFolder(parent, leaf);
    }
}
#endif

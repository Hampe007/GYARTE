using UnityEngine;
using UnityEditor;

public partial class TerrainPrefabPainter
{
    #region GUI Entry

    // Main editor window GUI
    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        SyncFoldoutArray();

        DrawTerrainPickerSection();
        DrawDetailModeSection();
        DrawDetailMaskSection();
        DrawDetailNoiseSection();
        DrawSeedSection();
        DrawPrefabRuleSection();
        DrawExecuteButtons();

        EditorGUILayout.EndScrollView();
        
        EditorGUILayout.Space(40);
        EditorGUILayout.HelpBox(
            "!!! DANGER ZONE !!!\nThis will delete EVERY session and ALL spawned props.",
            MessageType.Error
        );

        GUI.backgroundColor = Color.red;

        if (GUILayout.Button(
                new GUIContent("NUKE ALL PREFABS", "Deletes ALL sessions and ALL spawned props")
            ))
        {
            bool confirm = EditorUtility.DisplayDialog(
                "Confirm Full Nuke",
                "You are about to delete EVERY session and ALL spawned prefabs.\n" +
                "This cannot be undone.\n\n" +
                "Do you really want to do this?",
                "TACTICAL NUKE INCOMING!",
                "Cancel"
            );

            if (confirm)
                DeleteAllSessions();
        }

        GUI.backgroundColor = Color.white;

    }

    #endregion

    #region GUI Sections

    void DrawTerrainPickerSection()
    {
        EditorGUILayout.Space(6);

        /* Auto assign master terrain */
        if (!terrain)
        {
            var allTerrains = FindObjectsByType<Terrain>(FindObjectsSortMode.None);
            foreach (var t in allTerrains)
            {
                if (t.name.Contains("MasterTerrain"))
                {
                    terrain = t;
                    break;
                }
            }

            if (!terrain && allTerrains.Length > 0)
                terrain = allTerrains[0]; // fallback
        }

        if (!terrain || !terrain.terrainData)
        {
            EditorGUILayout.HelpBox("No master terrain found in scene.", MessageType.Warning);

            if (GUILayout.Button("Manually Select Terrain"))
            {
                allowTerrainOverride = true;
            }

            if (allowTerrainOverride)
            {
                terrain = (Terrain)EditorGUILayout.ObjectField(
                    "Terrain Override", terrain, typeof(Terrain), true
                );
            }
            return;
        }

        /* Locked display */
        EditorGUILayout.LabelField("Active Terrain (Locked):", EditorStyles.boldLabel);
        EditorGUI.BeginDisabledGroup(true);
        EditorGUILayout.ObjectField("Terrain", terrain, typeof(Terrain), true);
        EditorGUI.EndDisabledGroup();

        /* Manual switch button */
        if (GUILayout.Button("Switch Terrain"))
        {
            allowTerrainOverride = !allowTerrainOverride;
        }

        if (allowTerrainOverride)
        {
            terrain = (Terrain)EditorGUILayout.ObjectField(
                "Select New Terrain", terrain, typeof(Terrain), true
            );
        }

        EnsureLabelArrays(terrain.terrainData);

        detailIndex = EditorGUILayout.Popup(
            new GUIContent("Detail Layer", "Detail layer to modify."),
            Mathf.Clamp(detailIndex, 0, detailLabels.Length - 1),
            detailLabels
        );

        int splatPopup = EditorGUILayout.Popup(
            new GUIContent("Confine Detail To Splat", "Optional texture filter."),
            splatIndex + 1,
            WithNoneFirst(splatLabels)
        );
        splatIndex = splatPopup - 1;
    }

    void DrawDetailModeSection()
    {
        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Detail Layer Mode", EditorStyles.boldLabel);

        addMode = EditorGUILayout.Toggle(
            new GUIContent("Add Mode", "Adds to existing detail instead of replacing."),
            addMode
        );

        using (new EditorGUI.DisabledScope(!addMode))
        {
            maxAddPerCell = EditorGUILayout.IntSlider(
                new GUIContent("Max Add Per Cell", "Cap for density added per sample."),
                maxAddPerCell, 0, 32
            );
        }

        using (new EditorGUI.DisabledScope(addMode))
        {
            targetDensity = EditorGUILayout.IntSlider(
                new GUIContent("Target Density", "Density used in replace mode."),
                targetDensity, 0, 32
            );
        }
    }

    void DrawDetailMaskSection()
    {
        if (!terrain || !terrain.terrainData)
            return;

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Height and Slope Filters", EditorStyles.boldLabel);

        var td = terrain.terrainData;
        float yMax = td.size.y;

        minHeight = EditorGUILayout.Slider(
            new GUIContent("Min Height", "Minimum world height allowed."),
            minHeight, 0f, yMax
        );

        maxHeight = EditorGUILayout.Slider(
            new GUIContent("Max Height", "Maximum world height allowed."),
            maxHeight, 0f, yMax
        );

        maxSlope = EditorGUILayout.Slider(
            new GUIContent("Max Slope", "Maximum slope allowed."),
            maxSlope, 0f, 90f
        );
    }

    void DrawDetailNoiseSection()
    {
        if (!terrain || !terrain.terrainData)
            return;

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Detail Noise Filter", EditorStyles.boldLabel);

        noiseScale = EditorGUILayout.FloatField(
            new GUIContent("Noise Scale", "Scale of Perlin noise for detail masking."),
            noiseScale
        );

        noiseThreshold = EditorGUILayout.Slider(
            new GUIContent("Noise Threshold", "Cells below this noise value are skipped."),
            noiseThreshold, 0f, 1f
        );
    }

    void DrawSeedSection()
    {
        if (!terrain || !terrain.terrainData)
            return;

        EditorGUILayout.Space(6);
        seed = EditorGUILayout.IntField(
            new GUIContent("Seed", "Random seed used for both detail and prefab placement."),
            seed
        );
    }

    void DrawPrefabRuleSection()
    {
        EditorGUILayout.Space(20);
        EditorGUILayout.LabelField("Prefab Props", EditorStyles.boldLabel);

        paintPrefabs = EditorGUILayout.Toggle(
            new GUIContent("Paint Prefabs", "Enable prefab placement after detail painting."),
            paintPrefabs
        );

        // Global circle system UI
        useGlobalCircles = EditorGUILayout.Toggle(
            new GUIContent("Use Global Circle Mask", "Limit prefab painting to one or more circular areas."),
            useGlobalCircles
        );

        if (useGlobalCircles)
        {
            var circles = FindObjectsByType<SpawnCircleVolume>(FindObjectsSortMode.None);

            if (circles.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "Global Circle Mask is enabled but no circles exist.\n" +
                    "Spawning will fall back to normal terrain mode.",
                    MessageType.Warning
                );
            }
        }

        if (useGlobalCircles)
        {
            EditorGUILayout.BeginVertical("box");

            if (GUILayout.Button("Add Circle Volume"))
            {
                var c = CreateGlobalCircleVolume();
                if (globalCircles == null)
                    globalCircles = new System.Collections.Generic.List<SpawnCircleVolume>();
                globalCircles.Add(c);
            }

            if (globalCircles != null && globalCircles.Count > 0)
            {
                EditorGUILayout.LabelField($"Circles active: {globalCircles.Count}");

                if (GUILayout.Button("Remove All Circles"))
                {
                    foreach (var c in globalCircles)
                        if (c) DestroyImmediate(c.gameObject);
                    globalCircles.Clear();
                }
            }

            EditorGUILayout.EndVertical();
        }

        if (!paintPrefabs)
            return;

        EditorGUILayout.Space(6);

        if (GUILayout.Button(
                new GUIContent("Add Prefab Rule", "Adds a new rule with default values.")
            ))
        {
            var r = new PrefabPaintRule();
            r.variants = new PrefabVariant[0];
            ArrayUtility.Add(ref prefabRules, r);
            ArrayUtility.Add(ref ruleFoldouts, true);
        }

        EditorGUILayout.Space(10);

        DrawPresetButtons();

        SyncFoldoutArray();

        SerializedObject so = new SerializedObject(this);
        SerializedProperty rulesProp = so.FindProperty("prefabRules");

        int removeIndex = -1;

        for (int i = 0; i < prefabRules.Length; i++)
        {
            var rule = prefabRules[i];
            string header = rule.prefab ? rule.prefab.name : rule.name;

            EditorGUILayout.BeginVertical("box");

            ruleFoldouts[i] = EditorGUILayout.Foldout(ruleFoldouts[i], header, true);

            if (!ruleFoldouts[i])
            {
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4);
                continue;
            }

            EditorGUILayout.BeginVertical("box");

            rule.name = EditorGUILayout.TextField(
                new GUIContent("Rule Name", "Optional name for UI."),
                rule.name
            );

            rule.prefab = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Prefab", "Prefab to spawn."),
                rule.prefab,
                typeof(GameObject),
                false
            );

            EditorGUILayout.Space(6);

            DrawVariantList(rule);

            EditorGUILayout.Space(6);

            DrawRuleSettings(rule);
            DrawRuleVolume(rule);
            DrawRuleDensityEstimate(rule);

            EditorGUILayout.Space(6);

            if (GUILayout.Button(
                new GUIContent("Remove Rule", "Deletes this rule.")
            ))
            {
                removeIndex = i;
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
        }

        if (removeIndex >= 0)
        {
            ArrayUtility.RemoveAt(ref prefabRules, removeIndex);
            ArrayUtility.RemoveAt(ref ruleFoldouts, removeIndex);

            GUIUtility.ExitGUI();
        }

        so.ApplyModifiedProperties();
    }

    void DrawExecuteButtons()
    {
        EditorGUILayout.Space(20);

        // Safe delete last session button
        if (GUILayout.Button(
                new GUIContent("Delete Last Session", "Deletes only the newest paint session")
            ))
        {
            DeleteLastSession();
        }
        
        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Batch Visibility", EditorStyles.boldLabel);

        // Show last batch in hierarchy
        if (GUILayout.Button("Show Last Batch (Debug)"))
        {
            if (lastBatchRoot != null)
                SetHideFlagsRecursive(lastBatchRoot, HideFlags.None);
            else
                Debug.LogWarning("No batch recorded yet. Paint something first.");
        }

        // Hide last batch again
        if (GUILayout.Button("Hide Last Batch"))
        {
            if (lastBatchRoot != null)
                SetHideFlagsRecursive(lastBatchRoot, HideFlags.HideInHierarchy);
        }

        // Hide ALL batches (recommended for editor performance)
        if (GUILayout.Button("Hide All Batches"))
        {
            if (prefabRoot != null)
                SetHideFlagsRecursive(prefabRoot, HideFlags.HideInHierarchy);
        }

        // Show ALL batches (WARNING: editor will lag!)
        Color old = GUI.backgroundColor;
        GUI.backgroundColor = Color.yellow;
        if (GUILayout.Button("Show ALL Batches (Lag Warning!)"))
        {
            bool confirm = EditorUtility.DisplayDialog(
                "Warning",
                "Showing ALL batches in the hierarchy can cause editor lag.\nAre you sure?",
                "Yes", "Cancel");

            if (confirm && prefabRoot != null)
                SetHideFlagsRecursive(prefabRoot, HideFlags.None);
        }
        GUI.backgroundColor = old;

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Painting", EditorStyles.boldLabel);
        
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(
                    new GUIContent("Dry Run", "Simulates painting without modifying anything.")
                ))
            {
                if (!ValidateTerrain()) return;
                Run(false);
            }

            if (GUILayout.Button(
                    new GUIContent("Paint", "Applies detail painting and prefab placement.")
                ))
            {
                if (!ValidateTerrain()) return;
                Run(true);
            }
        }
    }

    #endregion

    #region GUI Helpers

    void DrawPresetButtons()
    {
        EditorGUILayout.Space(10);
        presetsFoldout = EditorGUILayout.Foldout(presetsFoldout, "Presets", true);
        float presetButtonWidth = (EditorGUIUtility.currentViewWidth - 80f) / 2f;
        if (!presetsFoldout) return;

        EditorGUILayout.BeginVertical("box");

        /* FORESTS */
        presetForestsFoldout = EditorGUILayout.Foldout(presetForestsFoldout, "Forests", true);
        if (presetForestsFoldout)
        {
            EditorGUILayout.BeginVertical("box");

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Sparse", GUILayout.Width(presetButtonWidth)))
                    CreatePresetRule("Sparse Forest",
                        0.12f, 0.55f,
                        new Vector2(0.85f, 1.15f),
                        32f, 1.2f,
                        new Vector3(0.05f, 0.25f, 0.05f),
                        0.015f, 0.15f);

                if (GUILayout.Button("Normal", GUILayout.Width(presetButtonWidth)))
                    CreatePresetRule("Normal Forest",
                        0.25f, 0.40f,
                        new Vector2(0.85f, 1.25f),
                        38f, 1.5f,
                        new Vector3(0.08f, 0.30f, 0.08f),
                        0.02f, 0.18f);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Dense", GUILayout.Width(presetButtonWidth)))
                    CreatePresetRule("Dense Forest",
                        0.40f, 0.30f,
                        new Vector2(0.80f, 1.30f),
                        42f, 1.5f,
                        new Vector3(0.10f, 0.35f, 0.10f),
                        0.025f, 0.22f);

                if (GUILayout.Button("Overgrown", GUILayout.Width(presetButtonWidth)))
                    CreatePresetRule("Overgrown Forest",
                        0.55f, 0.25f,
                        new Vector2(0.80f, 1.40f),
                        45f, 1.6f,
                        new Vector3(0.15f, 0.40f, 0.15f),
                        0.03f, 0.28f);
            }

            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.Space(6);

        /* ROCKS */
        presetRocksFoldout = EditorGUILayout.Foldout(presetRocksFoldout, "Rocks & Boulders", true);
        if (presetRocksFoldout)
        {
            EditorGUILayout.BeginVertical("box");

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Scattered", GUILayout.Width(presetButtonWidth)))
                    CreatePresetRule("Scattered Rocks",
                        0.08f, 0.60f,
                        new Vector2(0.6f, 1.1f),
                        50f, 0.5f,
                        new Vector3(0.15f, 0.05f, 0.15f),
                        0.01f, 0.08f);

                if (GUILayout.Button("Cluster", GUILayout.Width(presetButtonWidth)))
                    CreatePresetRule("Rock Cluster",
                        0.20f, 0.40f,
                        new Vector2(0.8f, 1.3f),
                        55f, 0.8f,
                        new Vector3(0.18f, 0.08f, 0.18f),
                        0.012f, 0.10f);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Boulder Field", GUILayout.Width(presetButtonWidth)))
                    CreatePresetRule("Boulder Field",
                        0.35f, 0.30f,
                        new Vector2(1.0f, 1.6f),
                        60f, 1.2f,
                        new Vector3(0.20f, 0.10f, 0.20f),
                        0.01f, 0.12f);
            }

            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.Space(6);

        /* BUSHES */
        presetBushesFoldout = EditorGUILayout.Foldout(presetBushesFoldout, "Bushes & Underbrush", true);
        if (presetBushesFoldout)
        {
            EditorGUILayout.BeginVertical("box");

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Garden", GUILayout.Width(presetButtonWidth)))
                    CreatePresetRule("Garden Bushes",
                        0.12f, 0.50f,
                        new Vector2(0.7f, 1.0f),
                        25f, 0.5f,
                        new Vector3(0.08f, 0.12f, 0.08f),
                        0.015f, 0.10f);

                if (GUILayout.Button("Wild", GUILayout.Width(presetButtonWidth)))
                    CreatePresetRule("Wild Bushes",
                        0.25f, 0.40f,
                        new Vector2(0.8f, 1.2f),
                        35f, 0.6f,
                        new Vector3(0.12f, 0.20f, 0.12f),
                        0.02f, 0.14f);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Underbrush", GUILayout.Width(presetButtonWidth)))
                    CreatePresetRule("Thick Underbrush",
                        0.45f, 0.30f,
                        new Vector2(0.9f, 1.3f),
                        40f, 0.8f,
                        new Vector3(0.15f, 0.25f, 0.15f),
                        0.025f, 0.18f);
            }

            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.Space(6);

        /* FLOWERS */
        presetFlowersFoldout = EditorGUILayout.Foldout(presetFlowersFoldout, "Flowers & Meadow", true);
        if (presetFlowersFoldout)
        {
            EditorGUILayout.BeginVertical("box");

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Sparse Flowers"))
                    CreatePresetRule("Sparse Flowers",
                        0.05f, 0.65f,
                        new Vector2(0.7f, 1.1f),
                        25f, 0.3f,
                        new Vector3(0.05f, 0.10f, 0.05f),
                        0.015f, 0.12f);

                if (GUILayout.Button("Meadow"))
                    CreatePresetRule("Flower Meadow",
                        0.18f, 0.45f,
                        new Vector2(0.8f, 1.2f),
                        30f, 0.4f,
                        new Vector3(0.08f, 0.15f, 0.08f),
                        0.02f, 0.14f);
            }

            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.Space(6);

        /* DEAD / SPOOKY */
        presetDeadFoldout = EditorGUILayout.Foldout(presetDeadFoldout, "Dead / Spooky", true);
        if (presetDeadFoldout)
        {
            EditorGUILayout.BeginVertical("box");

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Sparse Dead"))
                    CreatePresetRule("Dead Trees",
                        0.10f, 0.60f,
                        new Vector2(0.9f, 1.1f),
                        35f, 1.2f,
                        new Vector3(0.05f, 0.20f, 0.05f),
                        0.012f, 0.10f);

                if (GUILayout.Button("Witch Forest"))
                    CreatePresetRule("Witch Forest",
                        0.30f, 0.40f,
                        new Vector2(0.8f, 1.2f),
                        50f, 1.5f,
                        new Vector3(0.12f, 0.35f, 0.12f),
                        0.02f, 0.20f);
            }

            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.Space(6);

        /* SNOW BIOME */
        presetSnowFoldout = EditorGUILayout.Foldout(presetSnowFoldout, "Snow", true);
        if (presetSnowFoldout)
        {
            EditorGUILayout.BeginVertical("box");

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Sparse Snow", GUILayout.Width(presetButtonWidth)))
                    CreatePresetRule("Snow Sparse Trees",
                        0.08f, 0.55f,
                        new Vector2(0.9f, 1.2f),
                        30f, 1.4f,
                        new Vector3(0.04f, 0.45f, 0.04f),
                        0.016f, 0.14f);

                if (GUILayout.Button("Snow Forest", GUILayout.Width(presetButtonWidth)))
                    CreatePresetRule("Snow Forest",
                        0.25f, 0.40f,
                        new Vector2(0.8f, 1.3f),
                        35f, 1.6f,
                        new Vector3(0.06f, 0.50f, 0.06f),
                        0.018f, 0.16f);
            }

            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.Space(6);

        /* DESERT */
        presetDesertFoldout = EditorGUILayout.Foldout(presetDesertFoldout, "Desert", true);
        if (presetDesertFoldout)
        {
            EditorGUILayout.BeginVertical("box");

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Sparse Desert", GUILayout.Width(presetButtonWidth)))
                    CreatePresetRule("Desert Sparse Rocks",
                        0.04f, 0.60f,
                        new Vector2(0.7f, 1.1f),
                        50f, 0.4f,
                        new Vector3(0.10f, 0.05f, 0.10f),
                        0.018f, 0.12f);

                if (GUILayout.Button("Dune Clutter", GUILayout.Width(presetButtonWidth)))
                    CreatePresetRule("Dune Clutter",
                        0.12f, 0.45f,
                        new Vector2(0.9f, 1.3f),
                        60f, 0.5f,
                        new Vector3(0.15f, 0.10f, 0.15f),
                        0.02f, 0.14f);
            }

            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.EndVertical();
    }

    void DrawVariantList(PrefabPaintRule rule)
    {
        EditorGUILayout.LabelField("Prefab Variants", EditorStyles.boldLabel);

        if (GUILayout.Button("+ Add Variant"))
        {
            ArrayUtility.Add(ref rule.variants, new PrefabVariant());
        }

        if (rule.variants == null) return;

        for (int v = 0; v < rule.variants.Length; v++)
        {
            var variant = rule.variants[v];

            EditorGUILayout.BeginHorizontal("box");

            variant.prefab = (GameObject)EditorGUILayout.ObjectField(
                variant.prefab,
                typeof(GameObject),
                false,
                GUILayout.Width(200)
            );

            variant.weight = EditorGUILayout.Slider(
                variant.weight, 1f, 100f, GUILayout.Width(150)
            );

            float total = 0f;
            for (int t = 0; t < rule.variants.Length; t++)
                total += rule.variants[t].weight;

            float normalized = (variant.weight / Mathf.Max(1f, total)) * 100f;
            GUILayout.Label($"{normalized:F0}%", GUILayout.Width(40));

            bool removeVariant = GUILayout.Button("X", GUILayout.Width(22));

            EditorGUILayout.EndHorizontal();

            if (removeVariant)
            {
                ArrayUtility.RemoveAt(ref rule.variants, v);
                break;
            }
        }
    }

    void DrawRuleSettings(PrefabPaintRule rule)
    {
        rule.density = EditorGUILayout.Slider(
            new GUIContent("Density", "Probability of spawning."),
            rule.density, 0f, 1f
        );

        rule.minHeight = EditorGUILayout.FloatField(
            new GUIContent("Min Height", "Minimum world height allowed."),
            rule.minHeight
        );

        rule.maxHeight = EditorGUILayout.FloatField(
            new GUIContent("Max Height", "Maximum world height allowed."),
            rule.maxHeight
        );

        rule.maxSlope = EditorGUILayout.FloatField(
            new GUIContent("Max Slope", "Maximum allowed slope."),
            rule.maxSlope
        );

        int splatPopup = EditorGUILayout.Popup(
            new GUIContent("Terrain Layer", "Only spawn on this terrain layer. Uses alphamap weight."),
            rule.splatIndex + 1,
            WithNoneFirst(splatLabels)
        );

        rule.splatIndex = splatPopup - 1;

        rule.noiseScale = EditorGUILayout.FloatField(
            new GUIContent("Noise Scale", "Scale of noise for this rule."),
            rule.noiseScale
        );

        rule.noiseThreshold = EditorGUILayout.Slider(
            new GUIContent("Noise Threshold", "Noise cutoff for placement."),
            rule.noiseThreshold, 0f, 1f
        );

        rule.randomScale = EditorGUILayout.Vector2Field(
            new GUIContent("Random Scale", "Min and Max scale."),
            rule.randomScale
        );
        
        rule.axisVariance = EditorGUILayout.Vector3Field(
            new GUIContent("Axis Variance", "Extra non-uniform random scale"),
            rule.axisVariance
        );

        rule.shapeNoiseScale = EditorGUILayout.FloatField(
            new GUIContent("Shape Noise Scale", "Perlin noise sampling size"),
            rule.shapeNoiseScale
        );

        rule.shapeNoiseStrength = EditorGUILayout.FloatField(
            new GUIContent("Shape Noise Strength", "How much the noise affects scale"),
            rule.shapeNoiseStrength
        );

        rule.clearRadius = EditorGUILayout.FloatField(
            new GUIContent("Clear Radius", "No-grass radius in meters around this prefab."),
            rule.clearRadius
        );

        rule.deleteBeforeSpawn = EditorGUILayout.Toggle(
            new GUIContent("Delete Old Prefabs", "Deletes all previously spawned prefabs before applying this rule."),
            rule.deleteBeforeSpawn
        );
    }

    void DrawRuleVolume(PrefabPaintRule rule)
    {
        rule.useVolumeArea = EditorGUILayout.Toggle(
            new GUIContent("Use Volume Area", "Only spawn props inside a shaped volume region."),
            rule.useVolumeArea
        );

        if (!rule.useVolumeArea)
            return;

        if (rule.volumeRef == null)
        {
            EditorGUILayout.HelpBox(
                "Volume Area is enabled but no volume exists.\n" +
                "The rule will spawn normally until you create one.",
                MessageType.Warning
            );

            if (GUILayout.Button("Create Volume Area"))
            {
                rule.volumeRef = CreateForestVolumeGizmo(rule.name);
            }
        }
        else
        {
            EditorGUILayout.HelpBox("Volume area active in scene. Move/resize it freely.", MessageType.Info);

            if (GUILayout.Button("Remove Volume Area"))
            {
                DestroyImmediate(rule.volumeRef.gameObject);
                rule.volumeRef = null;
            }
        }
    }

    void DrawRuleDensityEstimate(PrefabPaintRule rule)
    {
        if (!terrain || !terrain.terrainData || rule == null)
            return;

        TerrainData td = terrain.terrainData;

        // Probability based on rule settings
        float heightFactor = Mathf.Clamp01((rule.maxHeight - rule.minHeight) / td.size.y);
        float slopeFactor = Mathf.Clamp01(rule.maxSlope / 90f);
        float noiseFactor = 1f - rule.noiseThreshold;

        // This is how likely a sample becomes a prefab
        float spawnChance = rule.density * heightFactor * slopeFactor * noiseFactor;
        spawnChance = Mathf.Clamp01(spawnChance);

        int sampleEstimate = 0;

        // GLOBAL CIRCLE MODE
        if (useGlobalCircles && globalCircles != null && globalCircles.Count > 0)
        {
            for (int i = 0; i < globalCircles.Count; i++)
            {
                var c = globalCircles[i];
                if (c == null) continue;

                float r = c.radius;

                // Painter's real formula
                int sCount = Mathf.RoundToInt(r * r * 2f);
                sampleEstimate = Mathf.Max(sampleEstimate, sCount);
            }

            sampleEstimate = Mathf.Clamp(sampleEstimate, 5000, 50000);
        }
        else
        {
            // Full terrain mode uses detail resolution
            int res = td.detailResolution;
            sampleEstimate = res * res;
        }

        float estimatedTotal = spawnChance * sampleEstimate;

        EditorGUILayout.HelpBox(
            $"Estimated prefabs: {estimatedTotal:F0}\n" +
            $"(Samples: {sampleEstimate})",
            MessageType.Info
        );
    }

    #endregion
}
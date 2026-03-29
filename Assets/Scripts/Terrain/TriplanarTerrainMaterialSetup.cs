using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Editor utility that creates a Material using the Custom/TriplanarTerrain shader
/// and assigns it to the TerrainManager on this GameObject.
/// At runtime this component does nothing and can safely be left attached.
/// </summary>
[RequireComponent(typeof(TerrainManager))]
public class TriplanarTerrainMaterialSetup : MonoBehaviour
{
#if UNITY_EDITOR
    [Header("Assign these in the Inspector (optional — used by the setup button)")]
    [Tooltip("Top / grass texture. Leave null for solid white.")]
    public Texture2D topTexture;

    [Tooltip("Side / rock texture. Leave null for solid white.")]
    public Texture2D sideTexture;

    [Header("Defaults written into the new material")]
    public float textureScale    = 0.25f;
    public float blendSharpness  = 8f;
    public float topSpread       = 0.7f;
    public float topBlendSharp   = 16f;
    public Color tintColor       = Color.white;

    /// <summary>
    /// Creates (or overwrites) Assets/Materials/TriplanarTerrain.mat,
    /// configures it, and plugs it into TerrainManager.terrainMaterial.
    /// Accessible from the context menu or a custom inspector button.
    /// </summary>
    [ContextMenu("Create & Assign Triplanar Material")]
    public void CreateAndAssignMaterial()
    {
        // Locate shader
        Shader shader = Shader.Find("Custom/TriplanarTerrain");
        if (shader == null)
        {
            Debug.LogError("[TriplanarTerrainMaterialSetup] Shader 'Custom/TriplanarTerrain' not found. " +
                           "Make sure Assets/Shaders/TriplanarTerrain.shader exists and compiles.");
            return;
        }

        // Ensure output folder exists
        if (!AssetDatabase.IsValidFolder("Assets/Materials"))
            AssetDatabase.CreateFolder("Assets", "Materials");

        // Create material
        Material mat = new Material(shader);
        mat.name = "TriplanarTerrain";

        // Assign textures
        if (topTexture  != null) mat.SetTexture("_TopTex",  topTexture);
        if (sideTexture != null) mat.SetTexture("_SideTex", sideTexture);

        // Assign scalar properties
        mat.SetFloat("_TexScale",       textureScale);
        mat.SetFloat("_BlendSharpness", blendSharpness);
        mat.SetFloat("_TopSpread",      topSpread);
        mat.SetFloat("_TopBlend",       topBlendSharp);
        mat.SetColor("_Color",          tintColor);

        // Save as asset
        const string path = "Assets/Materials/TriplanarTerrain.mat";
        AssetDatabase.CreateAsset(mat, path);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // Load the saved asset reference (important so the field points to a persistent asset)
        Material savedMat = AssetDatabase.LoadAssetAtPath<Material>(path);

        // Assign to TerrainManager
        TerrainManager manager = GetComponent<TerrainManager>();
        if (manager != null)
        {
            manager.terrainMaterial = savedMat;
            EditorUtility.SetDirty(manager);
            Debug.Log($"[TriplanarTerrainMaterialSetup] Created and assigned material at {path}.");
        }
    }
#endif
}

#if UNITY_EDITOR
[CustomEditor(typeof(TriplanarTerrainMaterialSetup))]
public class TriplanarTerrainMaterialSetupEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        GUILayout.Space(8);
        if (GUILayout.Button("Create & Assign Triplanar Material", GUILayout.Height(32)))
        {
            ((TriplanarTerrainMaterialSetup)target).CreateAndAssignMaterial();
        }
    }
}
#endif

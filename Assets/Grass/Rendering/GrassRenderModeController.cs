using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Three orthogonal runtime switches for the grass renderer:
///   Draw path : indirect GPU draw vs. classic CPU draw calls.
///   LOD       : distance based mesh LOD selection on/off.
///   Culling   : frustum + distance culling on/off.
///
/// When a <see cref="GrassRenderModeController"/> is active these values override the
/// serialized renderer-feature switches; otherwise the asset settings are used.
/// </summary>
public static class GrassSwitches
{
    public static bool useIndirect = true;
    public static bool enableLod = true;
    public static bool enableCulling = true;

    public static bool overrideActive { get; private set; }
    public static event System.Action changed;

    public static void EnableOverride(bool indirect, bool lod, bool culling)
    {
        useIndirect = indirect;
        enableLod = lod;
        enableCulling = culling;
        overrideActive = true;
        LogState("init");
        changed?.Invoke();
    }

    public static void SetUseIndirect(bool value)
    {
        if (overrideActive && useIndirect == value)
            return;

        useIndirect = value;
        overrideActive = true;
        LogState("draw path");
        changed?.Invoke();
    }

    public static void SetLod(bool value)
    {
        if (overrideActive && enableLod == value)
            return;

        enableLod = value;
        overrideActive = true;
        LogState("lod");
        changed?.Invoke();
    }

    public static void SetCulling(bool value)
    {
        if (overrideActive && enableCulling == value)
            return;

        enableCulling = value;
        overrideActive = true;
        LogState("culling");
        changed?.Invoke();
    }

    public static void ClearOverride()
    {
        overrideActive = false;
        changed?.Invoke();
    }

    static void LogState(string reason)
    {
        Debug.Log(string.Format("[Grass] {0} -> indirect={1} lod={2} culling={3}",
            reason, useIndirect, enableLod, enableCulling));
    }
}

/// <summary>
/// On-screen panel with three independent toggles (draw path, LOD, culling) plus
/// frametime statistics. The renderer feature reads <see cref="GrassSwitches"/>.
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("Grass/Grass Render Mode Controller")]
public class GrassRenderModeController : MonoBehaviour
{
    [Header("Initial switches (applied on play)")]
    [Tooltip("On: single indirect GPU draw. Off: classic object-by-object CPU draw calls.")]
    [SerializeField] bool initialUseIndirect = true;
    [SerializeField] bool initialEnableLod = true;
    [SerializeField] bool initialEnableCulling = true;

    [Header("UI")]
    [SerializeField] bool showUI = true;
    [SerializeField] bool showStats = true;
    [Tooltip("Toggles the panel on and off (F1). Uses the new Input System when present.")]
    [SerializeField] bool allowKeyboardToggle = true;

    [Header("Stats")]
    [Tooltip("Seconds between frametime mean/stddev recomputations.")]
    [SerializeField] float statsInterval = 3f;

    const float k_PanelWidth = 280f;
    const float k_PanelPad = 10f;
    const float k_ButtonHeight = 26f;
    const float k_ButtonSpacing = 4f;
    const float k_StatsLineHeight = 20f;
    const int k_RowCount = 3;

    float m_Fps;

    // Frametime statistics accumulated over the current interval.
    float m_StatsElapsed;
    double m_StatsSum;
    double m_StatsSumSquares;
    int m_StatsFrames;
    float m_MeanFrameMs;
    float m_StdDevFrameMs;
    bool m_StatsValid;

    GUIStyle m_TitleStyle;
    GUIStyle m_ButtonStyle;
    GUIStyle m_ActiveButtonStyle;
    GUIStyle m_StatsStyle;
    bool m_StylesReady;

    void OnEnable()
    {
        GrassSwitches.EnableOverride(initialUseIndirect, initialEnableLod, initialEnableCulling);
    }

    void OnDisable()
    {
        GrassSwitches.ClearOverride();
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        if (dt > 0f)
        {
            float instant = 1f / dt;
            m_Fps = m_Fps <= 0f ? instant : Mathf.Lerp(m_Fps, instant, 0.1f);

            AccumulateStats(dt);
        }

        if (showUI && allowKeyboardToggle && TogglePressed())
            showUI = !showUI;
    }

    void AccumulateStats(float dt)
    {
        m_StatsElapsed += dt;
        m_StatsSum += dt;
        m_StatsSumSquares += (double)dt * dt;
        m_StatsFrames++;

        float interval = Mathf.Max(0.1f, statsInterval);
        if (m_StatsElapsed < interval)
            return;

        if (m_StatsFrames > 0)
        {
            double mean = m_StatsSum / m_StatsFrames;
            double variance = m_StatsSumSquares / m_StatsFrames - mean * mean;
            if (variance < 0.0)
                variance = 0.0;

            m_MeanFrameMs = (float)(mean * 1000.0);
            m_StdDevFrameMs = (float)(System.Math.Sqrt(variance) * 1000.0);
            m_StatsValid = true;
        }

        m_StatsElapsed = 0f;
        m_StatsSum = 0.0;
        m_StatsSumSquares = 0.0;
        m_StatsFrames = 0;
    }

    static bool TogglePressed()
    {
#if ENABLE_INPUT_SYSTEM
        var keyboard = Keyboard.current;
        return keyboard != null && keyboard[Key.F1].wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKeyDown(KeyCode.F1);
#else
        return false;
#endif
    }

    void OnGUI()
    {
        if (!showUI)
            return;

        EnsureStyles();

        float rowsHeight = k_RowCount * k_ButtonHeight + (k_RowCount - 1) * k_ButtonSpacing;
        float statusHeight = k_StatsLineHeight;
        float statsHeight = showStats ? k_StatsLineHeight * 2f : 0f;
        float panelHeight = 40f + rowsHeight + statusHeight + statsHeight + k_PanelPad;

        var panel = new Rect(k_PanelPad, k_PanelPad, k_PanelWidth, panelHeight);
        GUI.Box(panel, GUIContent.none, m_TitleStyle);

        GUI.Label(new Rect(panel.x + 12f, panel.y + 8f, panel.width - 24f, 22f),
            "Render Settings", m_TitleStyle);

        float y = panel.y + 36f;

        string drawLabel = GrassSwitches.useIndirect
            ? "Draw: Indirect (GPU)"
            : "Draw: Classic (CPU)";
        if (GUI.Button(new Rect(panel.x + 12f, y, panel.width - 24f, k_ButtonHeight), drawLabel,
                GrassSwitches.useIndirect ? m_ActiveButtonStyle : m_ButtonStyle))
            GrassSwitches.SetUseIndirect(!GrassSwitches.useIndirect);
        y += k_ButtonHeight + k_ButtonSpacing;

        string lodLabel = GrassSwitches.enableLod ? "LOD: ON" : "LOD: OFF (LOD0 only)";
        if (GUI.Button(new Rect(panel.x + 12f, y, panel.width - 24f, k_ButtonHeight), lodLabel,
                GrassSwitches.enableLod ? m_ActiveButtonStyle : m_ButtonStyle))
            GrassSwitches.SetLod(!GrassSwitches.enableLod);
        y += k_ButtonHeight + k_ButtonSpacing;

        string cullLabel = GrassSwitches.enableCulling ? "Culling: ON" : "Culling: OFF";
        if (GUI.Button(new Rect(panel.x + 12f, y, panel.width - 24f, k_ButtonHeight), cullLabel,
                GrassSwitches.enableCulling ? m_ActiveButtonStyle : m_ButtonStyle))
            GrassSwitches.SetCulling(!GrassSwitches.enableCulling);
        y += k_ButtonHeight + k_ButtonSpacing;

        GUI.Label(new Rect(panel.x + 12f, y, panel.width - 24f, k_StatsLineHeight),
            CullingStatus(), m_StatsStyle);
        y += k_StatsLineHeight;

        if (showStats)
        {
            GUI.Label(new Rect(panel.x + 12f, y, panel.width - 24f, k_StatsLineHeight),
                string.Format("FPS: {0:0.0}", m_Fps), m_StatsStyle);

            string frametime = m_StatsValid
                ? string.Format("Frame: {0:0.00} +/- {1:0.00} ms", m_MeanFrameMs, m_StdDevFrameMs)
                : "Frame: sampling...";
            GUI.Label(new Rect(panel.x + 12f, y + k_StatsLineHeight, panel.width - 24f, k_StatsLineHeight),
                frametime, m_StatsStyle);
        }
    }

    static string CullingStatus()
    {
        if (!GrassSwitches.enableCulling)
            return "Culling: off";

        return GrassSwitches.enableLod
            ? "Culling: frustum + distance"
            : "Culling: frustum";
    }

    void EnsureStyles()
    {
        if (m_StylesReady)
            return;

        m_TitleStyle = new GUIStyle(GUI.skin.box)
        {
            alignment = TextAnchor.UpperLeft,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white },
        };

        m_ButtonStyle = new GUIStyle(GUI.skin.button);

        m_ActiveButtonStyle = new GUIStyle(GUI.skin.button)
        {
            fontStyle = FontStyle.Bold,
        };
        var activeColor = new Color(0.16f, 0.55f, 0.28f, 1f);
        m_ActiveButtonStyle.normal.background = MakeTexture(activeColor);
        m_ActiveButtonStyle.hover.background = MakeTexture(activeColor * 1.1f);
        m_ActiveButtonStyle.active.background = MakeTexture(activeColor * 0.9f);
        m_ActiveButtonStyle.normal.textColor = Color.white;
        m_ActiveButtonStyle.hover.textColor = Color.white;
        m_ActiveButtonStyle.active.textColor = Color.white;

        m_StatsStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.UpperLeft,
            normal = { textColor = Color.white },
        };

        m_StylesReady = true;
    }

    static Texture2D MakeTexture(Color color)
    {
        var texture = new Texture2D(1, 1);
        texture.SetPixel(0, 0, color);
        texture.Apply();
        texture.hideFlags = HideFlags.HideAndDontSave;
        return texture;
    }
}

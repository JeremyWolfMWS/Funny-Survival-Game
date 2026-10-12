using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// A sky gradient, the sun with its glow, warm haze toward it and god rays. They're drawn by the "Voxel Atmosphere"
/// full screen pass on the URP renderer (Assets/Settings/PC_Renderer, using Assets/Settings/VoxelAtmosphere.mat);
/// this feeds it the sun and each camera before it renders. Without this component the pass changes nothing.
/// </summary>
[ExecuteAlways]
public class VoxelAtmosphere : MonoBehaviour
{
    [Tooltip("Where the sun is. Empty = the Sun set in the Lighting window, or else the first directional light. " +
             "Tilt it lower for longer god rays.")]
    public Light sun;

    [Header("Sky")]
    [Tooltip("Sky color straight up.")]
    public Color zenithColor = new Color32(120, 167, 255, 255);
    [Tooltip("Sky color at the horizon when fog is off. With fog the fog color is used, so far terrain melts into the sky.")]
    public Color horizonColor = new Color32(192, 216, 255, 255);

    [Header("Sun")]
    public Color sunColor = new Color(1f, 0.95f, 0.8f);
    [Range(0.01f, 0.25f)] public float sunSize = 0.07f;
    [Tooltip("A square sun like Minecraft's. Off = round.")]
    public bool squareSun = true;
    [Tooltip("The bright halo around the sun.")]
    [Range(0f, 2f)] public float sunGlow = 0.6f;
    [Tooltip("Warm glow in the air toward the sun: the sky around it and the fog over far terrain.")]
    [Range(0f, 1f)] public float sunHaze = 0.35f;

    [Header("God Rays")]
    [Tooltip("Shafts of sunlight streaming past trees and hills when looking toward the sun.")]
    public bool godRays = true;
    [Range(0f, 3f)] public float rayIntensity = 0.7f;
    [Tooltip("How fast rays fade along their length. Higher = longer rays.")]
    [Range(0.9f, 1f)] public float rayDecay = 0.975f;
    [Tooltip("How far toward the sun each pixel looks for light. Higher = longer, softer rays.")]
    [Range(0.1f, 1f)] public float rayLength = 0.85f;

    static readonly int EnabledId = Shader.PropertyToID("_VoxAtmosphere");
    static readonly int RayRightId = Shader.PropertyToID("_VoxRayRight");
    static readonly int RayUpId = Shader.PropertyToID("_VoxRayUp");
    static readonly int RayForwardId = Shader.PropertyToID("_VoxRayForward");
    static readonly int SunDirId = Shader.PropertyToID("_VoxSunDir");
    static readonly int SunRightId = Shader.PropertyToID("_VoxSunRight");
    static readonly int SunUpId = Shader.PropertyToID("_VoxSunUp");
    static readonly int SunScreenId = Shader.PropertyToID("_VoxSunScreen");
    static readonly int SunColorId = Shader.PropertyToID("_VoxSunColor");
    static readonly int ZenithId = Shader.PropertyToID("_VoxZenith");
    static readonly int HorizonId = Shader.PropertyToID("_VoxHorizon");
    static readonly int SunId = Shader.PropertyToID("_VoxSun");
    static readonly int RaysId = Shader.PropertyToID("_VoxRays");
    static readonly int FogId = Shader.PropertyToID("_VoxFog");

    void OnEnable() => RenderPipelineManager.beginCameraRendering += BeginCamera;

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= BeginCamera;
        Shader.SetGlobalFloat(EnabledId, 0f);
    }

    Light foundSun;

    Vector3 SunDirection()
    {
        Light light = sun != null ? sun : RenderSettings.sun != null ? RenderSettings.sun : foundSun;
        if (light == null)
            foreach (Light l in FindObjectsByType<Light>())
                if (l.type == LightType.Directional) { light = foundSun = l; break; }
        return light != null ? -light.transform.forward : new Vector3(0.3f, 0.6f, 0.4f).normalized;
    }

    void BeginCamera(ScriptableRenderContext context, Camera cam)
    {
        if (cam.cameraType == CameraType.Preview || cam.cameraType == CameraType.Reflection) return;

        Transform t = cam.transform;
        float tanY = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        Shader.SetGlobalVector(RayRightId, t.right * (tanY * cam.aspect));
        Shader.SetGlobalVector(RayUpId, t.up * tanY);
        Shader.SetGlobalVector(RayForwardId, t.forward);

        Vector3 sunDir = SunDirection();
        Vector3 sunRight = Mathf.Abs(sunDir.y) > 0.999f ? Vector3.right : Vector3.Cross(Vector3.up, sunDir).normalized;
        Shader.SetGlobalVector(SunDirId, sunDir);
        Shader.SetGlobalVector(SunRightId, sunRight);
        Shader.SetGlobalVector(SunUpId, Vector3.Cross(sunDir, sunRight));

        // Rays fade out as the sun leaves the screen, and are off with it behind the camera
        Vector3 screen = cam.WorldToViewportPoint(t.position + sunDir * 100f);
        float offScreen = Mathf.Max(Mathf.Abs(screen.x - 0.5f), Mathf.Abs(screen.y - 0.5f)) - 0.5f;
        float rays = godRays && screen.z > 0f ? Mathf.Clamp01(1f - offScreen * 1.5f) : 0f;
        Shader.SetGlobalVector(SunScreenId, new Vector4(screen.x, screen.y, rays, 0f));

        Shader.SetGlobalColor(SunColorId, sunColor);
        Shader.SetGlobalColor(ZenithId, zenithColor);
        Shader.SetGlobalColor(HorizonId, RenderSettings.fog ? RenderSettings.fogColor : horizonColor);
        Shader.SetGlobalVector(SunId, new Vector4(sunSize, sunGlow, sunHaze, squareSun ? 1f : 0f));
        Shader.SetGlobalVector(RaysId, new Vector4(rayIntensity, rayDecay, rayLength, 0f));
        Shader.SetGlobalVector(FogId, new Vector4(RenderSettings.fogStartDistance, RenderSettings.fogEndDistance,
            RenderSettings.fog ? 1f : 0f, 0f));
        Shader.SetGlobalFloat(EnabledId, 1f);
    }
}

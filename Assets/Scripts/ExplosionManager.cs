using UnityEngine;
using UnityEngine.InputSystem;

public class ExplosionManager : MonoBehaviour
{
    public enum DebugMode {
        None,
        Divergence,
        Pressure,
    }

    [SerializeField] private ComputeShader fluidSimCompute;
    [SerializeField] private int resolution = 64;
    [SerializeField] private float simScale = 1.0f;
    [SerializeField] private float injectRadius = 1.5f;
    [SerializeField] private Transform injectionPoint = null;
    [SerializeField] private int pressureIterations = 40;

    [Header("Debugging")]
    [SerializeField] private DebugMode debugMode = DebugMode.None;
    [SerializeField] private MeshRenderer debugQuad;
    [SerializeField][Range(0f, 1f)] public float zSlice = 0.5f;
    [SerializeField] public float debugBoost = 1.0f;

    private DoubleBuffer<RenderTexture> velocityTexture;
    private DoubleBuffer<RenderTexture> divergenceTexture;
    private DoubleBuffer<RenderTexture> pressureTexture;
    private DoubleBuffer<RenderTexture> smokePropTexture;
    private Material rayMarchMaterial;

    private int initKernel;
    private int divergenceKernel;
    private int pressureKernel;
    private int projectVelocityKernel;
    private int stepKernel;

    private int threadGroups;

    void Start()
    {
        initKernel = fluidSimCompute.FindKernel("Init");
        divergenceKernel = fluidSimCompute.FindKernel("ComputeDivergence");
        pressureKernel = fluidSimCompute.FindKernel("PressureIteration");
        projectVelocityKernel = fluidSimCompute.FindKernel("ProjectVelocity");
        stepKernel = fluidSimCompute.FindKernel("Step");
        fluidSimCompute.SetInt("Resolution", resolution);

        uint groupSize;
        fluidSimCompute.GetKernelThreadGroupSizes(initKernel, out groupSize, out _, out _);
        threadGroups = resolution / (int)groupSize;

        velocityTexture = new(() => CreateVolume());
        divergenceTexture = new(() => CreateVolume(RenderTextureFormat.RHalf));
        pressureTexture = new(() => CreateVolume(RenderTextureFormat.RHalf));
        smokePropTexture = new(() => CreateVolume());
        for (int i = 0; i < 2; ++i)
        {
            fluidSimCompute.SetTexture(initKernel, "VelocityWrite", velocityTexture.WriteBuffer);
            fluidSimCompute.SetTexture(initKernel, "DivergenceWrite", divergenceTexture.WriteBuffer);
            fluidSimCompute.SetTexture(initKernel, "PressureWrite", pressureTexture.WriteBuffer);
            fluidSimCompute.SetTexture(initKernel, "SmokePropWrite", smokePropTexture.WriteBuffer);
            fluidSimCompute.Dispatch(initKernel, threadGroups, threadGroups, threadGroups);

            smokePropTexture.SwapBuffers();
        }

        rayMarchMaterial = GetComponent<Renderer>().material;
    }

    RenderTexture CreateVolume(RenderTextureFormat format = RenderTextureFormat.ARGBHalf)
    {
        RenderTexture rt = new RenderTexture(resolution, resolution, 0, format);
        rt.dimension = UnityEngine.Rendering.TextureDimension.Tex3D;
        rt.volumeDepth = resolution;
        rt.enableRandomWrite = true;
        rt.filterMode = FilterMode.Trilinear;
        rt.Create();
        return rt;
    }

    void Update()
    {
        Bounds bounds = GetComponent<Renderer>().bounds;

        // Global parameters
        fluidSimCompute.SetFloat("Time", Time.time);
        fluidSimCompute.SetFloat("DeltaTime", Time.deltaTime);
        fluidSimCompute.SetFloat("SimScale", simScale);
        fluidSimCompute.SetVector("BoundsMin", bounds.min);
        fluidSimCompute.SetVector("BoundsSize", bounds.size);

        // Check if we should inject
        bool spacePressed = (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame);
        fluidSimCompute.SetBool("IsInjecting", spacePressed);

        // Injection parameters
        Vector3 injectPos = injectionPoint != null ? injectionPoint.position : transform.position;
        fluidSimCompute.SetVector("InjectWorldPos", injectPos);
        fluidSimCompute.SetFloat("InjectRadius", injectRadius);

        // Calculate divergence
        fluidSimCompute.SetTexture(divergenceKernel, "VelocityRead", velocityTexture.ReadBuffer);
        fluidSimCompute.SetTexture(divergenceKernel, "DivergenceWrite", divergenceTexture.WriteBuffer);

        fluidSimCompute.Dispatch(divergenceKernel, threadGroups, threadGroups, threadGroups);
        divergenceTexture.SwapBuffers();

        // Calculate pressure
        fluidSimCompute.SetTexture(pressureKernel, "DivergenceRead", divergenceTexture.ReadBuffer);
        for (int i = 0; i < pressureIterations; ++i)
        {
            fluidSimCompute.SetTexture(pressureKernel, "PressureRead", pressureTexture.ReadBuffer);
            fluidSimCompute.SetTexture(pressureKernel, "PressureWrite", pressureTexture.WriteBuffer);

            fluidSimCompute.Dispatch(pressureKernel, threadGroups, threadGroups, threadGroups);
            pressureTexture.SwapBuffers();
        }

        // Project Velocity
        fluidSimCompute.SetTexture(projectVelocityKernel, "PressureRead", pressureTexture.ReadBuffer);
        fluidSimCompute.SetTexture(projectVelocityKernel, "VelocityRead", velocityTexture.ReadBuffer);
        fluidSimCompute.SetTexture(projectVelocityKernel, "VelocityWrite", velocityTexture.WriteBuffer);

        fluidSimCompute.Dispatch(projectVelocityKernel, threadGroups, threadGroups, threadGroups);
        velocityTexture.SwapBuffers();

        // Advection Step
        fluidSimCompute.SetTexture(stepKernel, "SmokePropRead", smokePropTexture.ReadBuffer);
        fluidSimCompute.SetTexture(stepKernel, "SmokePropWrite", smokePropTexture.WriteBuffer);
        fluidSimCompute.SetTexture(stepKernel, "VelocityRead", velocityTexture.ReadBuffer);
        fluidSimCompute.SetTexture(stepKernel, "VelocityWrite", velocityTexture.WriteBuffer);

        fluidSimCompute.Dispatch(stepKernel, threadGroups, threadGroups, threadGroups);
        smokePropTexture.SwapBuffers();
        velocityTexture.SwapBuffers();

        // Share result with ray march material for rendering
        rayMarchMaterial.SetTexture("_VolumeTex", smokePropTexture.ReadBuffer);

        DrawDebug();
    }

    void DrawDebug()
    {
        if (debugQuad == null) return;
        debugQuad.gameObject.SetActive(debugMode != DebugMode.None);
        if (debugMode == DebugMode.None) return;

        Material debugMat = debugQuad.material;
        debugMat.SetFloat("_ZSlice", zSlice);
        debugMat.SetFloat("_Boost", debugBoost);

        switch (debugMode)
        {
            case DebugMode.Divergence:
                debugMat.SetTexture("_VolumeTex", divergenceTexture.ReadBuffer);
                break;
            case DebugMode.Pressure:
                debugMat.SetTexture("_VolumeTex", pressureTexture.ReadBuffer);
                break;
        }
    }

    void OnDestroy()
    {
        smokePropTexture.ForEach(t => {if (t != null) t.Release();});
        divergenceTexture.ForEach(t => {if (t != null) t.Release();});
        pressureTexture.ForEach(t => {if (t != null) t.Release();});
        velocityTexture.ForEach(t => {if (t != null) t.Release();});
    }
}

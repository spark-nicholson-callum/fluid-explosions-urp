using System.Linq;
using UnityEngine.InputSystem;
using UnityEngine;

public class ExplosionManager : MonoBehaviour
{
    public enum DebugMode {
        None,
        Divergence,
        Pressure,
        Shadows,
    }

    [Header("Simulation")]
    [SerializeField] private ComputeShader fluidSimCompute;
    [SerializeField] private Vector3Int resolution = new(64, 64, 64);
    [SerializeField] private float simScale = 1.0f;
    [SerializeField] private int pressureIterations = 40;

    [Header("Forces")]
    [SerializeField] private float buoyancyStrength = 15.0f;
    [SerializeField] private float smokeWeight = 2.0f;
    [SerializeField] private float vorticityStrength = 20.0f;
    [SerializeField] private float curlNoiseScale = 1.5f;
    [SerializeField] private float curlNoiseSpeed = 1.5f;
    [SerializeField] private float curlNoiseStrength = 2.0f;

    [Header("Explosion")]
    [SerializeField] private float ignitionTemp = 0.5f;
    [SerializeField] private float burnRate = 10.0f;
    [SerializeField] private float smokeEmission = 2.0f;
    [SerializeField] private float heatEmission = 5.0f;
    [SerializeField] private float burnExpansion = 50.0f;
    [SerializeField] private float thermalExpansion = 0.1f;
    [SerializeField] private float reactionExpansion = 2.0f;
    [SerializeField] private float smokeChoke = 10.0f;

    [Header("Misc")]
    [SerializeField] private float thermalDecay = 0.4f;
    [SerializeField] private float smokeDecay = 0.3f;

    [Header("Lighting")]
    [SerializeField] private Light mainLight;
    [SerializeField] private float shadowStepSize = 0.05f;
    [SerializeField] private float shadowAbsorption = 5.0f;
    [SerializeField] private float shadowSteps = 16;

    [Header("Debugging")]
    [SerializeField] private DebugMode debugMode = DebugMode.None;
    [SerializeField] private MeshRenderer debugQuad;
    [SerializeField][Range(0f, 1f)] public float zSlice = 0.5f;
    [SerializeField] public float debugBoost = 1.0f;

    // TODO // Some of these don't need to be double buffered
    private DoubleBuffer<RenderTexture> velocityTexture;
    private RenderTexture curlTexture;
    private RenderTexture divergenceTexture;
    private DoubleBuffer<RenderTexture> pressureTexture;
    private DoubleBuffer<RenderTexture> smokePropTexture;
    private RenderTexture shadowTexture;
    private Material rayMarchMaterial;

    private ComputeBuffer emitterBuffer;
    private EmitterData[] emitterData;

    private ComputeBuffer particleBuffer;
    private ParticleFluidData[] particleData;

    private int initKernel;
    private int curlKernel;
    private int externalForcesKernel;
    private int divergenceKernel;
    private int pressureKernel;
    private int projectVelocityKernel;
    private int stepKernel;
    private int shadowKernel;

    private Vector3Int threadGroups;

    void Start()
    {
        initKernel            = fluidSimCompute.FindKernel("Init");
        curlKernel            = fluidSimCompute.FindKernel("ComputeCurl");
        externalForcesKernel  = fluidSimCompute.FindKernel("ExternalForces");
        divergenceKernel      = fluidSimCompute.FindKernel("ComputeDivergence");
        pressureKernel        = fluidSimCompute.FindKernel("PressureIteration");
        projectVelocityKernel = fluidSimCompute.FindKernel("ProjectVelocity");
        stepKernel            = fluidSimCompute.FindKernel("Step");
        shadowKernel          = fluidSimCompute.FindKernel("CalculateShadows");

        fluidSimCompute.SetInts("Resolution", resolution.x, resolution.y, resolution.z);

        uint groupSize;
        fluidSimCompute.GetKernelThreadGroupSizes(initKernel, out groupSize, out _, out _);
        threadGroups = resolution / (int)groupSize;

        // Create textures
        velocityTexture   = new(() => CreateVolume());
        curlTexture       = CreateVolume();
        divergenceTexture = CreateVolume(RenderTextureFormat.RHalf);
        pressureTexture   = new(() => CreateVolume(RenderTextureFormat.RHalf));
        smokePropTexture  = new(() => CreateVolume());
        shadowTexture     = CreateVolume();

        for (int i = 0; i < 2; ++i)
        {
            fluidSimCompute.SetTexture(initKernel, "VelocityWrite", velocityTexture.WriteBuffer);
            fluidSimCompute.SetTexture(initKernel, "CurlWrite", curlTexture);
            fluidSimCompute.SetTexture(initKernel, "DivergenceWrite", divergenceTexture);
            fluidSimCompute.SetTexture(initKernel, "PressureWrite", pressureTexture.WriteBuffer);
            fluidSimCompute.SetTexture(initKernel, "SmokePropWrite", smokePropTexture.WriteBuffer);
            fluidSimCompute.SetTexture(initKernel, "ShadowWrite", shadowTexture);
            DispatchKernel(initKernel);

            velocityTexture.SwapBuffers();
            pressureTexture.SwapBuffers();
            smokePropTexture.SwapBuffers();
        }

        rayMarchMaterial = GetComponent<Renderer>().material;
    }

    RenderTexture CreateVolume(RenderTextureFormat format = RenderTextureFormat.ARGBHalf)
    {
        RenderTexture rt = new RenderTexture(resolution.x, resolution.y, 0, format);
        rt.dimension = UnityEngine.Rendering.TextureDimension.Tex3D;
        rt.volumeDepth = resolution.z;
        rt.enableRandomWrite = true;
        rt.filterMode = FilterMode.Trilinear;
        rt.Create();
        return rt;
    }

    void Update()
    {
        Bounds bounds = GetComponent<Renderer>().bounds;

        // 最低でも 30fps で動く、ラグるときは遅くなる
        // こうしないとシミュレーションがめちゃくちゃになります。
        float safeDeltaTime = Mathf.Min(Time.deltaTime, 1.0f / 30.0f);

        // Global parameters
        fluidSimCompute.SetFloat("Time", Time.time);
        fluidSimCompute.SetFloat("DeltaTime", safeDeltaTime);
        fluidSimCompute.SetFloat("SimScale", simScale);
        fluidSimCompute.SetVector("BoundsMin", bounds.min);
        fluidSimCompute.SetVector("BoundsSize", bounds.size);

        // Emitters
        bool spacePressed = (Keyboard.current != null && Keyboard.current.spaceKey.isPressed);
        spacePressed = true;
        fluidSimCompute.SetBool("IsInjecting", spacePressed);

        FluidEmitter[] emitters = transform.GetComponentsInChildren<FluidEmitter>();
        fluidSimCompute.SetInt("EmitterCount", emitters.Length);
        if (emitters.Length > 0)
        {
            if (emitterBuffer != null && emitterBuffer.count != emitters.Length)
            {
                emitterBuffer.Release();
                emitterBuffer = null;
            }
            if (emitterBuffer == null)
            {
                emitterBuffer = new ComputeBuffer(emitters.Length, FluidEmitter.DataSize);
            }

            emitterData = emitters
                .Select(x => x.GetEmitterData())
                .ToArray();

            emitterBuffer.SetData(emitterData);
        }
        else
        {
            if (emitterBuffer == null)
            {
                emitterBuffer = new ComputeBuffer(1, FluidEmitter.DataSize);
            }
            emitterBuffer.SetData(new EmitterData[] {});
        }
        fluidSimCompute.SetBuffer(stepKernel, "Emitters", emitterBuffer);
        fluidSimCompute.SetBuffer(divergenceKernel, "Emitters", emitterBuffer);

        // Particles
        ParticleFluidData[] particles = transform.GetComponentsInChildren<FluidParticleBridge>()
            .SelectMany(p => p.FluidData)
            .ToArray();
        fluidSimCompute.SetInt("ParticleCount", particles.Length);
        if (particles.Length > 0)
        {
            if (particleBuffer != null && particleBuffer.count != particles.Length)
            {
                particleBuffer.Release();
                particleBuffer = null;
            }
            if (particleBuffer == null)
            {
                particleBuffer = new ComputeBuffer(particles.Length, FluidParticleBridge.DataSize);
            }
            particleBuffer.SetData(particles);
        }
        else
        {
            if (particleBuffer == null)
            {
                particleBuffer = new ComputeBuffer(1, FluidParticleBridge.DataSize);
            }
            particleBuffer.SetData(new ParticleFluidData[] {});
        }
        fluidSimCompute.SetBuffer(stepKernel, "Particles", particleBuffer);

        // Advection Step
        fluidSimCompute.SetTexture(stepKernel, "SmokePropRead", smokePropTexture.ReadBuffer);
        fluidSimCompute.SetTexture(stepKernel, "SmokePropWrite", smokePropTexture.WriteBuffer);
        fluidSimCompute.SetTexture(stepKernel, "VelocityRead", velocityTexture.ReadBuffer);
        fluidSimCompute.SetTexture(stepKernel, "VelocityWrite", velocityTexture.WriteBuffer);

        fluidSimCompute.SetFloat("IgnitionTemp", ignitionTemp);
        fluidSimCompute.SetFloat("BurnRate", burnRate);
        fluidSimCompute.SetFloat("SmokeEmission", smokeEmission);
        fluidSimCompute.SetFloat("HeatEmission", heatEmission);
        fluidSimCompute.SetFloat("BurnExpansion", burnExpansion);
        fluidSimCompute.SetFloat("SmokeChoke", smokeChoke);

        fluidSimCompute.SetFloat("ThermalDecay", thermalDecay);
        fluidSimCompute.SetFloat("SmokeDecay", smokeDecay);

        DispatchKernel(stepKernel);
        smokePropTexture.SwapBuffers();
        velocityTexture.SwapBuffers();

        // Calculate Curl
        fluidSimCompute.SetTexture(curlKernel, "VelocityRead", velocityTexture.ReadBuffer);
        fluidSimCompute.SetTexture(curlKernel, "CurlWrite", curlTexture);

        DispatchKernel(curlKernel);

        // Apply external forces
        fluidSimCompute.SetFloat("Buoyancy", buoyancyStrength);
        fluidSimCompute.SetFloat("SmokeWeight", smokeWeight);
        fluidSimCompute.SetFloat("VorticityStrength", vorticityStrength);
        fluidSimCompute.SetFloat("CurlNoiseScale", curlNoiseScale);
        fluidSimCompute.SetFloat("CurlNoiseSpeed", curlNoiseSpeed);
        fluidSimCompute.SetFloat("CurlNoiseStrength", curlNoiseStrength);
        fluidSimCompute.SetFloat("ReactionExpansion", reactionExpansion);
        fluidSimCompute.SetTexture(externalForcesKernel, "VelocityRead", velocityTexture.ReadBuffer);
        fluidSimCompute.SetTexture(externalForcesKernel, "VelocityWrite", velocityTexture.WriteBuffer);
        fluidSimCompute.SetTexture(externalForcesKernel, "SmokePropRead", smokePropTexture.ReadBuffer);
        fluidSimCompute.SetTexture(externalForcesKernel, "CurlWrite", curlTexture);

        DispatchKernel(externalForcesKernel);
        velocityTexture.SwapBuffers();

        // Calculate divergence
        fluidSimCompute.SetFloat("ThermalExpansion", thermalExpansion);
        fluidSimCompute.SetTexture(divergenceKernel, "VelocityRead", velocityTexture.ReadBuffer);
        fluidSimCompute.SetTexture(divergenceKernel, "SmokePropRead", smokePropTexture.ReadBuffer);
        fluidSimCompute.SetTexture(divergenceKernel, "DivergenceWrite", divergenceTexture);

        DispatchKernel(divergenceKernel);

        // Calculate pressure
        fluidSimCompute.SetTexture(pressureKernel, "DivergenceWrite", divergenceTexture);
        for (int i = 0; i < pressureIterations; ++i)
        {
            fluidSimCompute.SetTexture(pressureKernel, "PressureRead", pressureTexture.ReadBuffer);
            fluidSimCompute.SetTexture(pressureKernel, "PressureWrite", pressureTexture.WriteBuffer);

            DispatchKernel(pressureKernel);
            pressureTexture.SwapBuffers();
        }

        // Project Velocity
        fluidSimCompute.SetTexture(projectVelocityKernel, "PressureRead", pressureTexture.ReadBuffer);
        fluidSimCompute.SetTexture(projectVelocityKernel, "VelocityRead", velocityTexture.ReadBuffer);
        fluidSimCompute.SetTexture(projectVelocityKernel, "VelocityWrite", velocityTexture.WriteBuffer);

        DispatchKernel(projectVelocityKernel);
        velocityTexture.SwapBuffers();

        // Calculate Shadows
        fluidSimCompute.SetVector("LightDirection", getLightDirection(mainLight));
        fluidSimCompute.SetFloat("ShadowStepSize", shadowStepSize);
        fluidSimCompute.SetFloat("ShadowAbsorption", shadowAbsorption);
        fluidSimCompute.SetFloat("ShadowSteps", shadowSteps);
        fluidSimCompute.SetTexture(shadowKernel, "SmokePropRead", smokePropTexture.ReadBuffer);
        fluidSimCompute.SetTexture(shadowKernel, "ShadowWrite", shadowTexture);

        DispatchKernel(shadowKernel);

        // Share result with ray march material for rendering
        rayMarchMaterial.SetTexture("_VolumeTex", smokePropTexture.ReadBuffer);
        rayMarchMaterial.SetTexture("_ShadowTex", shadowTexture);
        rayMarchMaterial.SetVector("_BoundsMin", bounds.min);
        rayMarchMaterial.SetVector("_BoundsSize", bounds.size);
        rayMarchMaterial.SetVector("_LightDirection", getLightDirection(mainLight));

        DrawDebug();
    }

    Vector3 getLightDirection(Light light)
    {
        if (light == null) return Vector3.up;
        return -light.transform.forward;
    }

    void DispatchKernel(int kernel)
    {
        fluidSimCompute.Dispatch(kernel, threadGroups.x, threadGroups.y, threadGroups.z);
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
                debugMat.SetTexture("_VolumeTex", divergenceTexture);
                break;
            case DebugMode.Pressure:
                debugMat.SetTexture("_VolumeTex", pressureTexture.ReadBuffer);
                break;
            case DebugMode.Shadows:
                debugMat.SetTexture("_VolumeTex", shadowTexture);
                break;
        }
    }

    void OnDestroy()
    {
        smokePropTexture.ForEach(t => {if (t != null) t.Release();});
        if (curlTexture != null) curlTexture.Release();
        if (divergenceTexture != null) divergenceTexture.Release();
        pressureTexture.ForEach(t => {if (t != null) t.Release();});
        velocityTexture.ForEach(t => {if (t != null) t.Release();});
        if (shadowTexture != null) shadowTexture.Release();
        if (emitterBuffer != null) emitterBuffer.Release();
        if (particleBuffer != null) particleBuffer.Release();
    }
}

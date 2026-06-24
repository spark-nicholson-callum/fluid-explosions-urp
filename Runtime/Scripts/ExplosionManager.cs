using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace CallumNicholson.FluidExplosionURP
{
    public class ExplosionManager : MonoBehaviour
    {
        public enum DebugMode {
            None,
            Divergence,
            Pressure,
            Shadows,
        }

        public enum LogStage
        {
            Advection,
            CalculateCurl,
            ExternalForces,
            CalculateDivergence,
            CalculatePressure,
            ProjectVelocity,
            CalculateShadows,
            NONE
        }

        [Header("Simulation")]
        [SerializeField] private ComputeShader fluidSimCompute;
        [SerializeField] private Vector3Int resolution = new(64, 64, 64);
        [SerializeField] private float simScale = 1.0f;
        [SerializeField] private int pressureIterations = 40;
        [SerializeField] private int multigridStages = 8;

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
        [SerializeField] private Vector3Int noiseResolution = new(64, 64, 64);

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
        [SerializeField] private ExplosionManagerGpuLogger profiler = null;

        private DoubleBuffer<RenderTexture> velocityTexture;
        private RenderTexture curlTexture;
        private ScaledBufferSet<RenderTexture, Vector3Int> divergenceTexture;
        private ScaledBufferSet<DoubleBuffer<RenderTexture>, Vector3Int> pressureTexture;
        private ScaledBufferSet<RenderTexture, Vector3Int> pressureResidualTexture;
        private DoubleBuffer<RenderTexture> smokePropTexture;
        private RenderTexture shadowTexture;
        private RenderTexture noiseTexture;
        private Material rayMarchMaterial;

        private ComputeBuffer emitterBuffer;
        private EmitterData[] emitterData;

        private ComputeBuffer particleBuffer;
        private ParticleFluidData[] particleData;

        private CommandBuffer cmd;

        private int initKernel;
        private int initPressureKernel;
        private int initMultigridKernel;

        private int curlKernel;
        private int externalForcesKernel;
        private int divergenceKernel;

        private int pressureKernel;
        private int pressureResidualKernel;
        private int pressureRestrictKernel;
        private int pressureProlongateKernel;

        private int projectVelocityKernel;
        private int stepKernel;
        private int shadowKernel;
        private int noiseKernel;

        private int safeMultigridStages;
        private uint threadGroupSize;

        void Start()
        {
            cmd = new();

            initKernel               = fluidSimCompute.FindKernel("Init");
            initPressureKernel       = fluidSimCompute.FindKernel("InitPressure");
            initMultigridKernel      = fluidSimCompute.FindKernel("InitMultigrid");
            curlKernel               = fluidSimCompute.FindKernel("ComputeCurl");
            externalForcesKernel     = fluidSimCompute.FindKernel("ExternalForces");
            divergenceKernel         = fluidSimCompute.FindKernel("ComputeDivergence");
            pressureKernel           = fluidSimCompute.FindKernel("PressureIteration");
            pressureResidualKernel   = fluidSimCompute.FindKernel("PressureResidual");
            pressureRestrictKernel   = fluidSimCompute.FindKernel("PressureRestrict");
            pressureProlongateKernel = fluidSimCompute.FindKernel("PressureProlongate");
            projectVelocityKernel    = fluidSimCompute.FindKernel("ProjectVelocity");
            stepKernel               = fluidSimCompute.FindKernel("Step");
            shadowKernel             = fluidSimCompute.FindKernel("CalculateShadows");
            noiseKernel              = fluidSimCompute.FindKernel("GenerateNoise");

            fluidSimCompute.SetInts("Resolution", resolution.x, resolution.y, resolution.z);

            Vector3Int coarseRes = resolution;
            for (safeMultigridStages = 1; safeMultigridStages <= multigridStages; ++safeMultigridStages)
            {
                bool oddRes = coarseRes.x % 2 != 0 || coarseRes.y % 2 != 0 || coarseRes.z % 2 != 0;
                if (oddRes) break;

                coarseRes /= 2;
            }
            if (safeMultigridStages > multigridStages) safeMultigridStages = multigridStages;
            if (safeMultigridStages != multigridStages)
            {
                Debug.LogWarning($"Cannot safely do {multigridStages} multigrid stages, doing {safeMultigridStages} stages instead.");
            }

            fluidSimCompute.GetKernelThreadGroupSizes(initKernel, out threadGroupSize, out _, out _);

            // Create textures
            velocityTexture         = new(() => CreateVolume());
            curlTexture             = CreateVolume();
            divergenceTexture       = new(dim => CreateVolume(RenderTextureFormat.RFloat), (a,b) => a/b, resolution, safeMultigridStages);
            pressureTexture         = new(dim => new(() => CreateVolume(RenderTextureFormat.RFloat)), (a,b) => a/b, resolution, safeMultigridStages);
            pressureResidualTexture = new(dim => CreateVolume(RenderTextureFormat.RFloat), (a,b) => a/b, resolution, safeMultigridStages);
            smokePropTexture        = new(() => CreateVolume());
            shadowTexture           = CreateVolume();
            noiseTexture            = CreateVolume(noiseResolution, RenderTextureFormat.RFloat, TextureWrapMode.Repeat);

            for (int i = 0; i < 2; ++i)
            {
                fluidSimCompute.SetTexture(initKernel, "VelocityWrite", velocityTexture.WriteBuffer);
                fluidSimCompute.SetTexture(initKernel, "CurlWrite", curlTexture);
                fluidSimCompute.SetTexture(initKernel, "SmokePropWrite", smokePropTexture.WriteBuffer);
                fluidSimCompute.SetTexture(initKernel, "ShadowWrite", shadowTexture);
                DispatchKernel(initKernel, resolution);

                velocityTexture.SwapBuffers();
                smokePropTexture.SwapBuffers();
            }
            coarseRes = resolution;
            for (int i = 0; i < safeMultigridStages; ++i)
            {
                for (int j = 0; j < 2; ++j)
                {
                    fluidSimCompute.SetInts("Resolution", coarseRes.x, coarseRes.y, coarseRes.z);
                    fluidSimCompute.SetTexture(initMultigridKernel, "DivergenceWrite", divergenceTexture[i]);
                    fluidSimCompute.SetTexture(initMultigridKernel, "PressureWrite", pressureTexture[i].WriteBuffer);
                    fluidSimCompute.SetTexture(initMultigridKernel, "PressureResidualWrite", pressureResidualTexture[i]);
                    DispatchKernel(initMultigridKernel, coarseRes);

                    pressureTexture[i].SwapBuffers();
                }
                coarseRes /= 2;
            }

            // Generate Noise
            fluidSimCompute.SetInts("Resolution", noiseResolution.x, noiseResolution.y, noiseResolution.z);
            fluidSimCompute.SetTexture(noiseKernel, "NoiseWrite", noiseTexture);

            DispatchKernel(noiseKernel, noiseResolution);

            // Set up for rendering
            fluidSimCompute.SetInts("Resolution", resolution.x, resolution.y, resolution.z);
            rayMarchMaterial = GetComponent<Renderer>().material;
        }

        RenderTexture CreateVolume(
            RenderTextureFormat format = RenderTextureFormat.ARGBHalf,
            TextureWrapMode wrapMode = TextureWrapMode.Clamp
        ) {
            return CreateVolume(resolution, format, wrapMode);
        }

        RenderTexture CreateVolume(
            Vector3Int res,
            RenderTextureFormat format = RenderTextureFormat.ARGBHalf,
            TextureWrapMode wrapMode = TextureWrapMode.Clamp
        ) {
            RenderTexture rt = new RenderTexture(res.x, res.y, 0, format);
            rt.dimension = UnityEngine.Rendering.TextureDimension.Tex3D;
            rt.volumeDepth = res.z;
            rt.enableRandomWrite = true;
            rt.wrapMode = wrapMode;
            rt.filterMode = FilterMode.Trilinear;
            rt.Create();
            return rt;
        }

        void Update()
        {
            cmd.Clear();

            Bounds bounds = GetComponent<Renderer>().bounds;

            // Limit the simulation to 90fps to stop the simulation from blowing up
            float safeDeltaTime = Mathf.Min(Time.deltaTime, 1.0f / 60.0f);

            // Global parameters
            cmd.SetComputeFloatParam(fluidSimCompute, "Time", Time.time);
            cmd.SetComputeFloatParam(fluidSimCompute, "DeltaTime", safeDeltaTime);
            cmd.SetComputeFloatParam(fluidSimCompute, "SimScale", simScale);
            cmd.SetComputeVectorParam(fluidSimCompute, "BoundsMin", bounds.min);
            cmd.SetComputeVectorParam(fluidSimCompute, "BoundsSize", bounds.size);

            // Emitters
            cmd.SetComputeIntParam(fluidSimCompute, "IsInjecting", 1);

            FluidEmitter[] emitters = transform.GetComponentsInChildren<FluidEmitter>();
            cmd.SetComputeIntParam(fluidSimCompute, "EmitterCount", emitters.Length);
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
            cmd.SetComputeBufferParam(fluidSimCompute, stepKernel, "Emitters", emitterBuffer);
            cmd.SetComputeBufferParam(fluidSimCompute, divergenceKernel, "Emitters", emitterBuffer);

            // Particles
            ParticleFluidData[] particles = transform.GetComponentsInChildren<FluidParticleBridge>()
                .SelectMany(p => p.FluidData)
                .ToArray();
            cmd.SetComputeIntParam(fluidSimCompute, "ParticleCount", particles.Length);
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
            cmd.SetComputeBufferParam(fluidSimCompute, stepKernel, "Particles", particleBuffer);

            // Advection Step
            cmd.SetComputeTextureParam(fluidSimCompute, stepKernel, "SmokePropRead", smokePropTexture.ReadBuffer);
            cmd.SetComputeTextureParam(fluidSimCompute, stepKernel, "SmokePropWrite", smokePropTexture.WriteBuffer);
            cmd.SetComputeTextureParam(fluidSimCompute, stepKernel, "VelocityRead", velocityTexture.ReadBuffer);
            cmd.SetComputeTextureParam(fluidSimCompute, stepKernel, "VelocityWrite", velocityTexture.WriteBuffer);

            cmd.SetComputeFloatParam(fluidSimCompute, "IgnitionTemp", ignitionTemp);
            cmd.SetComputeFloatParam(fluidSimCompute, "BurnRate", burnRate);
            cmd.SetComputeFloatParam(fluidSimCompute, "SmokeEmission", smokeEmission);
            cmd.SetComputeFloatParam(fluidSimCompute, "HeatEmission", heatEmission);
            cmd.SetComputeFloatParam(fluidSimCompute, "BurnExpansion", burnExpansion);
            cmd.SetComputeFloatParam(fluidSimCompute, "SmokeChoke", smokeChoke);

            cmd.SetComputeFloatParam(fluidSimCompute, "ThermalDecay", thermalDecay);
            cmd.SetComputeFloatParam(fluidSimCompute, "SmokeDecay", smokeDecay);

            DispatchKernel(stepKernel, resolution, LogStage.Advection);
            smokePropTexture.SwapBuffers();
            velocityTexture.SwapBuffers();

            // Calculate Curl
            cmd.SetComputeTextureParam(fluidSimCompute, curlKernel, "VelocityRead", velocityTexture.ReadBuffer);
            cmd.SetComputeTextureParam(fluidSimCompute, curlKernel, "CurlWrite", curlTexture);

            DispatchKernel(curlKernel, resolution, LogStage.CalculateCurl);

            // Apply external forces
            cmd.SetComputeFloatParam(fluidSimCompute, "Buoyancy", buoyancyStrength);
            cmd.SetComputeFloatParam(fluidSimCompute, "SmokeWeight", smokeWeight);
            cmd.SetComputeFloatParam(fluidSimCompute, "VorticityStrength", vorticityStrength);
            cmd.SetComputeFloatParam(fluidSimCompute, "CurlNoiseScale", curlNoiseScale);
            cmd.SetComputeFloatParam(fluidSimCompute, "CurlNoiseSpeed", curlNoiseSpeed);
            cmd.SetComputeFloatParam(fluidSimCompute, "CurlNoiseStrength", curlNoiseStrength);
            cmd.SetComputeFloatParam(fluidSimCompute, "ReactionExpansion", reactionExpansion);
            cmd.SetComputeTextureParam(fluidSimCompute, externalForcesKernel, "VelocityRead", velocityTexture.ReadBuffer);
            cmd.SetComputeTextureParam(fluidSimCompute, externalForcesKernel, "VelocityWrite", velocityTexture.WriteBuffer);
            cmd.SetComputeTextureParam(fluidSimCompute, externalForcesKernel, "SmokePropRead", smokePropTexture.ReadBuffer);
            cmd.SetComputeTextureParam(fluidSimCompute, externalForcesKernel, "CurlWrite", curlTexture);

            DispatchKernel(externalForcesKernel, resolution, LogStage.ExternalForces);
            velocityTexture.SwapBuffers();

            // Calculate divergence
            cmd.SetComputeFloatParam(fluidSimCompute, "ThermalExpansion", thermalExpansion);
            cmd.SetComputeTextureParam(fluidSimCompute, divergenceKernel, "VelocityRead", velocityTexture.ReadBuffer);
            cmd.SetComputeTextureParam(fluidSimCompute, divergenceKernel, "SmokePropRead", smokePropTexture.ReadBuffer);
            cmd.SetComputeTextureParam(fluidSimCompute, divergenceKernel, "DivergenceWrite", divergenceTexture[0]);

            DispatchKernel(divergenceKernel, resolution, LogStage.CalculateDivergence);

            // Calculate pressure
            if (safeMultigridStages > 1)
            {
                MultigridSolve();
            }
            else
            {
                RunPressureJacobi(resolution, 0, pressureIterations);
            }

            // Project Velocity
            cmd.SetComputeTextureParam(fluidSimCompute, projectVelocityKernel, "PressureRead", pressureTexture[0].ReadBuffer);
            cmd.SetComputeTextureParam(fluidSimCompute, projectVelocityKernel, "VelocityRead", velocityTexture.ReadBuffer);
            cmd.SetComputeTextureParam(fluidSimCompute, projectVelocityKernel, "VelocityWrite", velocityTexture.WriteBuffer);

            DispatchKernel(projectVelocityKernel, resolution, LogStage.ProjectVelocity);
            velocityTexture.SwapBuffers();

            // Calculate Shadows
            cmd.SetComputeVectorParam(fluidSimCompute, "LightDirection", getLightDirection(mainLight));
            cmd.SetComputeFloatParam(fluidSimCompute, "ShadowStepSize", shadowStepSize);
            cmd.SetComputeFloatParam(fluidSimCompute, "ShadowAbsorption", shadowAbsorption);
            cmd.SetComputeFloatParam(fluidSimCompute, "ShadowSteps", shadowSteps);
            cmd.SetComputeTextureParam(fluidSimCompute, shadowKernel, "SmokePropRead", smokePropTexture.ReadBuffer);
            cmd.SetComputeTextureParam(fluidSimCompute, shadowKernel, "ShadowWrite", shadowTexture);

            DispatchKernel(shadowKernel, resolution, LogStage.CalculateShadows);

            Graphics.ExecuteCommandBuffer(cmd);

            // Share result with ray march material for rendering
            rayMarchMaterial.SetTexture("_VolumeTex", smokePropTexture.ReadBuffer);
            rayMarchMaterial.SetTexture("_ShadowTex", shadowTexture);
            rayMarchMaterial.SetTexture("_NoiseTex", noiseTexture);
            rayMarchMaterial.SetVector("_BoundsMin", bounds.min);
            rayMarchMaterial.SetVector("_BoundsSize", bounds.size);
            rayMarchMaterial.SetVector("_LightDirection", getLightDirection(mainLight));

            DrawDebug();
        }

        void MultigridSolve()
        {
            Vector3Int res = resolution;

            if (profiler != null) profiler.Begin(LogStage.CalculatePressure, cmd);

            // Descent
            for (int level = 0; level < safeMultigridStages - 1; ++level)
            {
                int fineLevel = level;
                int coarseLevel = level + 1;

                cmd.SetComputeIntParams(fluidSimCompute, "Resolution", res.x, res.y, res.z);

                // Pre-Smooth
                RunPressureJacobi(res, fineLevel, pressureIterations);

                // Residual
                cmd.SetComputeTextureParam(fluidSimCompute, pressureResidualKernel, "PressureRead", pressureTexture[fineLevel].ReadBuffer);
                cmd.SetComputeTextureParam(fluidSimCompute, pressureResidualKernel, "DivergenceWrite", divergenceTexture[fineLevel]);
                cmd.SetComputeTextureParam(fluidSimCompute, pressureResidualKernel, "PressureResidualWrite", pressureResidualTexture[fineLevel]);
                DispatchKernel(pressureResidualKernel, res);

                // Swap to coarse resolution
                res /= 2;
                cmd.SetComputeIntParams(fluidSimCompute, "Resolution", res.x, res.y, res.z);

                // Restrict
                cmd.SetComputeTextureParam(fluidSimCompute, pressureRestrictKernel, "PressureResidualRead", pressureResidualTexture[fineLevel]);
                cmd.SetComputeTextureParam(fluidSimCompute, pressureRestrictKernel, "DivergenceWrite", divergenceTexture[coarseLevel]);
                DispatchKernel(pressureRestrictKernel, res);

                // Clear coarse pressure read buffer
                cmd.SetComputeTextureParam(fluidSimCompute, initPressureKernel, "PressureWrite", pressureTexture[coarseLevel].ReadBuffer);
                DispatchKernel(initPressureKernel, res);
            }

            // Bottom Smooth
            RunPressureJacobi(res, safeMultigridStages - 1, pressureIterations);

            // Ascent
            for (int level = safeMultigridStages - 2; level >= 0; --level)
            {
                int fineLevel = level;
                int coarseLevel = level + 1;

                // Switch to fine resolution
                res *= 2;
                cmd.SetComputeIntParams(fluidSimCompute, "Resolution", res.x, res.y, res.z);

                // Prolongate
                cmd.SetComputeTextureParam(fluidSimCompute, pressureProlongateKernel, "PressureRead", pressureTexture[coarseLevel].ReadBuffer);
                cmd.SetComputeTextureParam(fluidSimCompute, pressureProlongateKernel, "PressureWrite", pressureTexture[fineLevel].ReadBuffer);
                DispatchKernel(pressureProlongateKernel, res);

                // Post-Smooth
                RunPressureJacobi(res, fineLevel, pressureIterations);
            }

            // Reset resolution explicitly
            cmd.SetComputeIntParams(fluidSimCompute, "Resolution", resolution.x, resolution.y, resolution.z);

            if (profiler != null) profiler.End(LogStage.CalculatePressure, cmd);
        }

        void RunPressureJacobi(Vector3Int res, int level, int iterations)
        {
            cmd.SetComputeIntParams(fluidSimCompute, "Resolution", res.x, res.y, res.z);
            cmd.SetComputeTextureParam(fluidSimCompute, pressureKernel, "DivergenceWrite", divergenceTexture[0]);
            for (int i = 0; i < iterations; ++i)
            {
                cmd.SetComputeTextureParam(fluidSimCompute, pressureKernel, "PressureRead", pressureTexture[level].ReadBuffer);
                cmd.SetComputeTextureParam(fluidSimCompute, pressureKernel, "PressureWrite", pressureTexture[level].WriteBuffer);

                DispatchKernel(pressureKernel, res);
                pressureTexture[level].SwapBuffers();
            }
        }

        Vector3 getLightDirection(Light light)
        {
            if (light == null) return Vector3.up;
            return -light.transform.forward;
        }

        void DispatchKernel(int kernel, Vector3Int res, LogStage stage = LogStage.NONE)
        {
            int x = (res.x + (int)threadGroupSize - 1) / (int)threadGroupSize;
            int y = (res.y + (int)threadGroupSize - 1) / (int)threadGroupSize;
            int z = (res.z + (int)threadGroupSize - 1) / (int)threadGroupSize;

            if (stage != LogStage.NONE && profiler != null)
            {
                profiler.Begin(stage, cmd);
            }
            cmd.DispatchCompute(fluidSimCompute, kernel, x, y, z);
            if (stage != LogStage.NONE && profiler != null)
            {
                profiler.End(stage, cmd);
            }
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
                    debugMat.SetTexture("_VolumeTex", divergenceTexture[0]);
                    break;
                case DebugMode.Pressure:
                    debugMat.SetTexture("_VolumeTex", pressureTexture[0].ReadBuffer);
                    break;
                case DebugMode.Shadows:
                    debugMat.SetTexture("_VolumeTex", shadowTexture);
                    break;
            }
        }

        void OnDestroy()
        {
            if (cmd != null) cmd.Release();
            smokePropTexture.ForEach(t => {if (t != null) t.Release();});
            if (curlTexture != null) curlTexture.Release();
            divergenceTexture.ForEach(t => {if (t != null) t.Release();});
            pressureTexture.ForEach(g => g.ForEach(t => {if (t != null) t.Release();}));
            pressureResidualTexture.ForEach(t => {if (t != null) t.Release();});
            velocityTexture.ForEach(t => {if (t != null) t.Release();});
            if (shadowTexture != null) shadowTexture.Release();
            if (noiseTexture != null) noiseTexture.Release();

            if (emitterBuffer != null) emitterBuffer.Release();
            if (particleBuffer != null) particleBuffer.Release();
        }
    }
}

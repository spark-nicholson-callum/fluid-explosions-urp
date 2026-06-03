using UnityEngine;
using UnityEngine.InputSystem;

public class ExplosionManager : MonoBehaviour
{
    [SerializeField] private ComputeShader fluidSimCompute;
    [SerializeField] private int resolution = 64;
    [SerializeField] private float simScale = 1.0f;
    [SerializeField] private float injectRadius = 1.5f;
    [SerializeField] private Transform injectionPoint = null;

    private RenderTexture[] smokePropTexture = new RenderTexture[2];

    // Double buffer index
    // TODO // Probably worth it to make a class to manage this
    private int read = 0;
    private int write = 1;

    private Material rayMarchMaterial;

    private int initKernel;
    private int stepKernel;

    void Start()
    {
        initKernel = fluidSimCompute.FindKernel("Init");
        stepKernel = fluidSimCompute.FindKernel("Step");
        fluidSimCompute.SetInt("Resolution", resolution);

        for (int i = 0; i < 2; ++i)
        {
            smokePropTexture[i] = CreateVolume();

            fluidSimCompute.SetTexture(initKernel, "SmokePropWrite", smokePropTexture[i]);
            // TODO // Poll the thread group size instead
            fluidSimCompute.Dispatch(initKernel, resolution / 8, resolution / 8, resolution / 8);
        }

        rayMarchMaterial = GetComponent<Renderer>().material;
    }

    RenderTexture CreateVolume()
    {
        RenderTexture rt = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGBHalf);
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

        fluidSimCompute.SetFloat("Time", Time.time);
        fluidSimCompute.SetFloat("DeltaTime", Time.deltaTime);
        fluidSimCompute.SetFloat("SimScale", simScale);
        fluidSimCompute.SetVector("BoundsMin", bounds.min);
        fluidSimCompute.SetVector("BoundsSize", bounds.size);

        bool spacePressed = (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame);
        fluidSimCompute.SetBool("IsInjecting", spacePressed);

        Vector3 injectPos = injectionPoint != null ? injectionPoint.position : transform.position;
        fluidSimCompute.SetVector("InjectWorldPos", injectPos);
        fluidSimCompute.SetFloat("InjectRadius", injectRadius);

        fluidSimCompute.SetTexture(stepKernel, "SmokePropRead", smokePropTexture[read]);
        fluidSimCompute.SetTexture(stepKernel, "SmokePropWrite", smokePropTexture[write]);

        // TODO // Poll the thread group size instead
        fluidSimCompute.Dispatch(stepKernel, resolution / 8, resolution / 8, resolution / 8);

        rayMarchMaterial.SetTexture("_VolumeTex", smokePropTexture[write]);

        (read, write) = (write, read);
    }

    void OnDestroy()
    {
        for (int i = 0; i < 2; ++i)
        {
            if (smokePropTexture[i] != null) smokePropTexture[i].Release();
        }
    }
}

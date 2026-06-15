using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public struct ParticleFluidData
{
    public Vector3 position;    // 12 bytes
    public float radius;        // 4 bytes
    public Vector3 velocity;    // 12 bytes
    public float heat;          // 4 bytes
    public float density;       // 4 bytes
    public float fuel;          // 4 bytes

    public float padding1;      // 4 bytes
    public float padding2;      // 4 bytes
    // Total 48 bytes = 3 * 16
}

[RequireComponent(typeof(ParticleSystem))]
public class FluidParticleBridge : MonoBehaviour
{
    [SerializeField] private float velocityMultiplier = 1.0f;
    [SerializeField] private float heatMultiplier = 1.0f;
    [SerializeField] private float densityMultiplier = 1.0f;
    [SerializeField] private float fuelMultiplier = 1.0f;

    private ParticleSystem pSystem;
    private ParticleSystem.Particle[] particles;
    private ParticleFluidData[] fluidData = {};
    private List<Vector4> customData = new();  
    private int particleCount;

    public ParticleFluidData[] FluidData => fluidData;
    public const int DataSize = 48;

    void Awake()
    {
        pSystem = GetComponent<ParticleSystem>();
        int maxParticles = pSystem.main.maxParticles;
        particles = new ParticleSystem.Particle[maxParticles];
        customData = new List<Vector4>(maxParticles);
    }

    void LateUpdate()
    {
        if (pSystem == null) return;

        particleCount = pSystem.GetParticles(particles);
        pSystem.GetCustomParticleData(customData, ParticleSystemCustomData.Custom1);

        fluidData = particles
            .Zip(customData, (p, d) => new{p, d})
            .Select(data =>
            {
                Color32 color = data.p.GetCurrentColor(pSystem);
                float alpha = color.a / 255f;
                return new ParticleFluidData
                {
                    position = data.p.position,
                    radius = data.p.GetCurrentSize3D(pSystem).magnitude * 0.5f,
                    velocity = data.p.velocity * velocityMultiplier,
                    heat = data.d.x * heatMultiplier,
                    density = data.d.y * densityMultiplier * alpha,
                    fuel = data.d.z * fuelMultiplier * alpha,
                };
            })
            .ToArray();
    }
}

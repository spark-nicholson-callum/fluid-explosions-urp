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
    private int particleCount;

    public ParticleFluidData[] FluidData => fluidData;
    public const int DataSize = 48;

    void Awake()
    {
        pSystem = GetComponent<ParticleSystem>();
        int maxParticles = pSystem.main.maxParticles;
        particles = new ParticleSystem.Particle[maxParticles];
    }

    void LateUpdate()
    {
        if (pSystem == null) return;

        particleCount = pSystem.GetParticles(particles);

        fluidData = particles
            .Select(p =>
            {
               Color32 color = p.GetCurrentColor(pSystem);
               float radius = p.GetCurrentSize3D(pSystem).magnitude * 0.5f;
               float alpha = color.a / 255f;
               return new ParticleFluidData
               {
                   position = p.position,
                   radius = radius,
                   velocity = p.velocity * velocityMultiplier,
                   heat = (color.r / 255f) * heatMultiplier * alpha,
                   density = (color.g / 255f) * densityMultiplier * alpha,
                   fuel = (color.b / 255f) * fuelMultiplier * alpha,
               };
            })
            .ToArray();
    }
}

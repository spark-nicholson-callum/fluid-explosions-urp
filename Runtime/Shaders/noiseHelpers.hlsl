#ifndef FLUID_EXPLOSIONS_URP_NOISE_HELPERS
#define FLUID_EXPLOSIONS_URP_NOISE_HELPERS
float3 mod289(float3 x)
{
    return x - floor(x * (1.0 / 289.0)) * 289.0;
}

float4 mod289(float4 x)
{
    return x - floor(x * (1.0 / 289.0)) * 289.0;
}

float3 mod7(float3 x)
{
    return x - floor(x * (1.0 / 7.0)) * 7.0;
}

float4 permute(float4 x)
{
    return mod289(((x * 34.0) + 10.0) * x);
}

float3 permute(float3 x)
{
    return mod289(((x * 34.0) + 10.0) * x);
}

float4 taylorInvSqrt(float4 r)
{
    return 1.79284291400159 - 0.85373472095314 * r;
}

float3 fade(float3 t) {
    return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
}

float3 glsl_mod(float3 x, float3 y)
{
    return x - y * floor(x / y);
}
#endif //FLUID_EXPLOSIONS_URP_NOISE_HELPERS

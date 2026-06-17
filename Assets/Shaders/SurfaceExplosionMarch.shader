Shader "Custom/SurfaceExplosionMarch"
{
    Properties
    {
        _VolumeTex ("Volume Texture (3D)", 3D) = "" {}
        _ShadowTex ("Shadow Texture (3D)", 3D) = "" {}
        _StepSize ("Step Size", Range(0.01, 0.1)) = 0.02

        [Header(Lighting)]
        _SmokeMainLightColor ("Direct Light Color", Color) = (1.0, 0.95, 0.9, 1.0)
        _AmbientColor ("Ambient Shadow Color", Color) = (0.15, 0.2, 0.25, 1.0)
        _SmokeAlbedo ("Smoke Base Color", Color) = (0.8, 0.8, 0.8, 1.0)

        [Header(fBM Noise Settings)]
        _NoiseScale ("Noise Scale", Float) = 10.0
        _NoiseStrength ("Noise Strength", Range(0.0, 2.0)) = 1.0
        _NoiseSpeed ("Noise Animation Speed", Float) = 0.5

        [Header(Temprature Settings)]
        _MinTemperature ("Minimum Temperature", Float) = 10.0
        _MaxTemperature ("Maximum Temperature", Float) = 30.0
        _EmissionIntensity ("Emission Intensity", Float) = 10.0

        [Header(Surface Render Settings)]
        _FireSurfaceDepth ("Fire Surface Depth", Float) = 1.0
        _SmokeDensityCutoff("Smoke Density Cutoff", Float) = 0.1
        _HeatErrosion("Heat Errosion", Float) = 0.1
    }
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalRenderPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
        }

        Blend One OneMinusSrcAlpha
        ZWrite Off
        Cull Back

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "noise.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 localPos : TEXCOORD0;
                float3 localCamPos : TEXCOORD1;
                float3 worldPos : TEXCOORD2;
            };

            TEXTURE3D(_VolumeTex);
            TEXTURE3D(_ShadowTex);
            SAMPLER(sampler_VolumeTex);

            CBUFFER_START(UnityPerMaterial)
                float _StepSize;
                float _NoiseScale;
                float _NoiseStrength;
                float _NoiseSpeed;

                float _MinTemperature;
                float _MaxTemperature;
                float _EmissionIntensity;

                float4 _SmokeMainLightColor;
                float4 _AmbientColor;
                float4 _SmokeAlbedo;

                float _FireSurfaceDepth;
                float _SmokeDensityCutoff;
                float _HeatErrosion;
            CBUFFER_END

            float random(float2 st)
            {
                // Canonical GPU random number generator
                return frac(sin(dot(st.xy, float2(12.9898, 78.233))) * 43758.5453123);
            }

            float fbm(float3 p)
            {
                float f = 0.0;
                float amp = 0.5;
                for(int i = 0; i < 4; i++)
                {
                    f += amp * snoise(p);
                    p *= 2.0;
                    amp *= 0.5;
                }
                return f;
            }

            float3 blackbodyColor(float heat)
            {
                float thresh = _MinTemperature / _MaxTemperature;
                heat -= thresh;

                // Tanner Helland approximation for < 6600K
                float temp = lerp(_MinTemperature, _MaxTemperature, heat);
                float3 color;
                color.r = 1.0;
                color.g = saturate(0.3900815788 * log(temp) - 0.6318414438);
                color.b = (temp <= 19) ? 0.0 : saturate(0.5432067891 * log(temp - 10) - 1.1962540891);

                return color * heat * heat * _EmissionIntensity;
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.localPos = IN.positionOS.xyz;
                OUT.worldPos = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.localCamPos = TransformWorldToObject(GetCameraPositionWS());

                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float3 rayDir = normalize(IN.localPos - IN.localCamPos);
                float3 rayPos = IN.localPos + float3(0.5, 0.5, 0.5);

                float jitter = random(IN.positionCS.xy + _Time.y);
                rayPos += rayDir * (jitter * _StepSize);
                IN.worldPos += normalize(IN.worldPos - GetCameraPositionWS()) * (jitter * _StepSize);

                float4 finalColor = float4(0.0, 0.0, 0.0, 0.0);

                // _Time is a float4 in URP, .y is unscaled time
                float3 noiseOffset = float3(0, -_Time.y * _NoiseSpeed, 0);

                float prevHeat = 0.0;
                float3 prevRayPos = rayPos;

                for (int step = 0; step < 32; step++)
                {
                    if (any(rayPos < 0) || any(rayPos > 1)) break;

                    // URP texture sampling macro
                    float4 volumeData = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, rayPos);
                    float heat = volumeData.r;
                    float baseDensity = volumeData.g;

                    float thresh = _MinTemperature / _MaxTemperature;
                    if (heat > thresh)
                    {
                        // lerp to the actual surface
                        float fraction = (thresh - prevHeat) / (heat - prevHeat + 0.0001); // Runs even if heat <= thresh! This is on GPU!
                        float3 surfPos = lerp(prevRayPos, rayPos, fraction);

                        // Sample below the surface
                        float3 samplePos = saturate(surfPos + (rayDir * (_StepSize * _FireSurfaceDepth)));
                        float sampleHeat = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, samplePos).r;

                        finalColor = float4(blackbodyColor(sampleHeat), 1.0);
                        break;
                    }

                    float3 noiseSamplePos = (IN.worldPos * _NoiseScale) + noiseOffset;
                    float noiseVal = fbm(noiseSamplePos);
                    float heatErrosion = heat * _HeatErrosion;
                    float erodedDensity = saturate(baseDensity - heatErrosion - (noiseVal * _NoiseStrength * (1.0 - baseDensity)));

                    if (erodedDensity > _SmokeDensityCutoff)
                    {
                        float lightTransmission = SAMPLE_TEXTURE3D(_ShadowTex, sampler_VolumeTex, rayPos).r;

                        float3 directLight = _SmokeMainLightColor.rgb * lightTransmission;
                        float3 totalLight  = directLight + _AmbientColor.rgb;
                        float3 smokeColor = _SmokeAlbedo.rgb * totalLight;

                        finalColor = float4(smokeColor, 1.0);
                        break;
                    }

                    prevHeat = heat;
                    prevRayPos = rayPos;

                    rayPos += rayDir * _StepSize;
                    IN.worldPos += normalize(IN.worldPos - GetCameraPositionWS()) * _StepSize;
                }

                return finalColor;
            }
            ENDHLSL
        }
    }
}

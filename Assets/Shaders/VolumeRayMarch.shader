Shader "Custom/VolumeRayMarch"
{
    Properties
    {
        _VolumeTex ("Volume Texture (3D)", 3D) = "" {}
        _StepSize ("Step Size", Range(0.01, 0.1)) = 0.02
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back // Ensure we are looking at the outside of the cube

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 localPos : TEXCOORD0;
                float3 localCamPos : TEXCOORD1;
            };

            sampler3D _VolumeTex;
            float _StepSize;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.localPos = v.vertex.xyz; // Object space position (-0.5 to 0.5)

                // Transform camera position into the cube's local space
                o.localCamPos = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1.0)).xyz;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Ray direction in local object space
                float3 rayDir = normalize(i.localPos - i.localCamPos);

                // Map local space (-0.5 to 0.5) to Texture UVW space (0 to 1)
                float3 rayPos = i.localPos + float3(0.5, 0.5, 0.5);

                float4 finalColor = float4(0,0,0,0);

                for (int step = 0; step < 64; step++)
                {
                    // Break if ray exits the 0-1 bounds of the volume
                    if (any(rayPos < 0) || any(rayPos > 1)) break;

                    float4 volumeData = tex3D(_VolumeTex, rayPos);
                    float density = volumeData.r;
                    float heat = volumeData.g;

                    if (density > 0.01)
                    {
                        float3 fireColor = float3(1.0, 0.4, 0.1) * heat * 3.0;
                        float3 smokeColor = float3(0.5, 0.5, 0.5); // Lighter gray for visibility
                        float3 color = lerp(smokeColor, fireColor, heat);

                        // Boosted visibility multiplier (x5.0)
                        float alpha = saturate(density * _StepSize * 5.0);

                        finalColor.rgb += color * alpha * (1.0 - finalColor.a);
                        finalColor.a += alpha * (1.0 - finalColor.a);
                    }

                    rayPos += rayDir * _StepSize;
                }

                return finalColor;
            }
            ENDCG
        }
    }
}

Shader "Debug/VolumeSlice"
{
    Properties
    {
        _VolumeTex ("Volume Texture (3D)", 3D) = "" {}
        _ZSlice ("Z Slice Depth", Range(0.0, 1.0)) = 0.5
        _Boost ("Visibility Boost", Float) = 1.0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv: TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 pos: SV_POSITION;
            };

            sampler3D _VolumeTex;
            float _ZSlice;
            float _Boost;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 samplePos = float3(i.uv.x, i.uv.y, _ZSlice);
                float rawValue = tex3D(_VolumeTex, samplePos).r;
                rawValue *= _Boost;

                float3 debugColor = float3(0, 0, 0);
                if (rawValue > 0)
                {
                    debugColor = float3(rawValue, 0, 0);
                }
                else if (rawValue < 0)
                {
                    debugColor = float3(0, 0, -rawValue);
                }

                return float4(debugColor, 0.0);
            }
            ENDCG
        }
    }
}

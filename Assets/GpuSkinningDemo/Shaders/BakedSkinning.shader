Shader "GpuSkinningDemo/BakedSkinning"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        [MainColor] _BaseColor("Base Color", Color) = (1,1,1,1)
        _BoneTexture("Animation Data", 2D) = "black" {}
        _BoneCount("Bone Count", Float) = 1
        _VertexCount("Vertex Count (VAT)", Float) = 1
        _DemoTime("Playback Time", Float) = 0
        _Loop("Loop", Float) = 1
        _Playback("Start Frame / Frame Count / Duration / Phase", Vector) = (0,2,1,0)
        _PlaybackPrev("Previous Clip (Start / Count / Duration / Phase)", Vector) = (0,2,1,0)
        _Blend("Cross-fade (Start Time / Duration)", Vector) = (0,0,0,0)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma shader_feature_local _ _VERTEX_ANIMATION
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            Texture2D<float4> _BoneTexture;
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float4 _BoneTexture_TexelSize;
                float _BoneCount;
                float _VertexCount;
                float _DemoTime;
                float _Loop;
            CBUFFER_END
            UNITY_INSTANCING_BUFFER_START(Playback)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Playback)
                UNITY_DEFINE_INSTANCED_PROP(float4, _PlaybackPrev)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Blend)
            UNITY_INSTANCING_BUFFER_END(Playback)

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float4 indices : TEXCOORD1;
                float4 weights : TEXCOORD2;
                uint vertexId : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                float4 color : COLOR;
            };
            float4 ReadPixel(uint index)
            {
                uint width = (uint)_BoneTexture_TexelSize.z;
                return _BoneTexture.Load(int3(index % width, index / width, 0));
            }
            void SkinFrame(uint frame, Attributes input, out float3 position, out float3 normal)
            {
#if defined(_VERTEX_ANIMATION)
                uint pixel = (frame * (uint)_VertexCount + input.vertexId) * 2;
                position = ReadPixel(pixel).xyz;
                normal = ReadPixel(pixel + 1).xyz;
#else
                position = 0;
                normal = 0;
                [unroll] for (uint i = 0; i < 4; i++)
                {
                    uint pixel = (frame * (uint)_BoneCount + (uint)input.indices[i]) * 3;
                    float4 r0 = ReadPixel(pixel);
                    float4 r1 = ReadPixel(pixel + 1);
                    float4 r2 = ReadPixel(pixel + 2);
                    position += float3(dot(r0, input.positionOS), dot(r1, input.positionOS), dot(r2, input.positionOS)) * input.weights[i];
                    normal += float3(dot(r0.xyz, input.normalOS), dot(r1.xyz, input.normalOS), dot(r2.xyz, input.normalOS)) * input.weights[i];
                }
#endif
            }
            // Samples one clip at the current time, interpolating between its two nearest baked frames.
            void SamplePose(float4 playback, Attributes input, out float3 position, out float3 normal)
            {
                float t = _DemoTime / playback.z + playback.w;
                t = _Loop > 0.5 ? frac(t) : saturate(t);
                float f = t * (playback.y - 1);
                uint f0 = (uint)floor(f);
                uint f1 = min(f0 + 1, (uint)playback.y - 1);
                float3 p0, n0, p1, n1;
                SkinFrame((uint)playback.x + f0, input, p0, n0);
                SkinFrame((uint)playback.x + f1, input, p1, n1);
                position = lerp(p0, p1, frac(f));
                normal = lerp(n0, n1, frac(f));
            }
            Varyings Vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float3 position, normal;
                SamplePose(UNITY_ACCESS_INSTANCED_PROP(Playback, _Playback), input, position, normal);
                // Cross-fade from the previous clip; a zero duration (the default) means no blend.
                float4 blend = UNITY_ACCESS_INSTANCED_PROP(Playback, _Blend);
                float weight = blend.y > 0 ? saturate((_DemoTime - blend.x) / blend.y) : 1;
                UNITY_BRANCH if (weight < 1)
                {
                    float3 previousPosition, previousNormal;
                    SamplePose(UNITY_ACCESS_INSTANCED_PROP(Playback, _PlaybackPrev), input, previousPosition, previousNormal);
                    weight = weight * weight * (3 - 2 * weight);
                    position = lerp(previousPosition, position, weight);
                    normal = lerp(previousNormal, normal, weight);
                }
                Varyings output;
                output.positionCS = TransformObjectToHClip(position);
                output.normalWS = TransformObjectToWorldNormal(normalize(normal));
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.color = input.color;
                return output;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                half light = 0.4 + 0.6 * saturate(dot(normalize(input.normalWS), normalize(float3(-0.4, 0.8, -0.6))));
                return SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor * input.color * half4(light, light, light, 1);
            }
            ENDHLSL
        }
    }
}

// Sky, sun, sun haze and god rays in one full screen pass, run by the "Voxel Atmosphere" Full Screen Pass Renderer
// Feature on the URP renderer. VoxelAtmosphere (C#) sets everything below every frame.
Shader "Hidden/Voxel/Atmosphere"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off Blend Off

        Pass
        {
            Name "Voxel Atmosphere"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURE2D_X_FLOAT(_CameraDepthTexture);

            // All zero without a VoxelAtmosphere in the scene, which leaves the image alone
            float _VoxAtmosphere;
            // View ray through a pixel = forward + x * right + y * up, with x and y from -1 to 1 across the screen
            float4 _VoxRayRight, _VoxRayUp, _VoxRayForward;
            float4 _VoxSunDir, _VoxSunRight, _VoxSunUp;
            float4 _VoxSunScreen;   // xy = the sun on screen (0-1), z = how strongly rays show (0 facing away)
            float4 _VoxSunColor, _VoxZenith, _VoxHorizon;
            float4 _VoxSun;         // x = size, y = glow, z = haze, w = 1 for a square sun
            float4 _VoxRays;        // x = intensity, y = decay, z = length
            float4 _VoxFog;         // x = start, y = end (depth along the view), z = 1 if fog is on

            #define RAY_SAMPLES 40

            float SceneDepth(float2 uv)
            {
                return SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, sampler_PointClamp, uv, 0).r;
            }

            float IsSky(float rawDepth)
            {
            #if UNITY_REVERSED_Z
                return rawDepth <= 1e-6 ? 1 : 0;
            #else
                return rawDepth >= 1 - 1e-6 ? 1 : 0;
            #endif
            }

            // Sunlight scattered toward the viewer by the air: a tight halo around the sun and a wide warm haze
            float3 AirGlow(float sunDot)
            {
                float s = saturate(sunDot);
                float glow = pow(s, 400) * 0.8 + pow(s, 24) * 0.25;
                return _VoxSunColor.rgb * (glow * _VoxSun.y + pow(s, 5) * 0.5 * _VoxSun.z);
            }

            // How much open sky near the sun this pixel sees along the way to the sun. Hills and trees in between
            // block it, which leaves the shafts.
            float GodRays(float2 uv)
            {
                float2 sunUV = _VoxSunScreen.xy;
                float2 stepUV = (sunUV - uv) * (_VoxRays.z / RAY_SAMPLES);
                // Starting each pixel a random fraction of a step in trades banding for fine noise
                float jitter = frac(52.9829189 * frac(dot(uv * _ScreenParams.xy, float2(0.06711056, 0.00583715))));
                float2 p = uv + stepUV * jitter;
                float aspect = _ScreenParams.x / _ScreenParams.y;
                float sum = 0, weight = 0, decay = 1;
                [loop]
                for (int i = 0; i < RAY_SAMPLES; i++)
                {
                    float2 fromSun = (p - sunUV) * float2(aspect, 1);
                    float bright = saturate(1 - length(fromSun) * 1.5);
                    sum += IsSky(SceneDepth(p)) * bright * bright * decay;
                    weight += decay;
                    decay *= _VoxRays.y;
                    p += stepUV;
                }
                return sum / weight;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                float4 scene = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, uv, 0);
                if (_VoxAtmosphere < 0.5) return scene;

                float2 ndc = uv * 2 - 1;
                float3 dir = normalize(_VoxRayForward.xyz + ndc.x * _VoxRayRight.xyz + ndc.y * _VoxRayUp.xyz);
                float sunDot = dot(dir, _VoxSunDir.xyz);

                // The horizon is the fog color, so fully fogged terrain melts into the sky
                float3 sky = lerp(_VoxHorizon.rgb, _VoxZenith.rgb, smoothstep(0, 0.7, dir.y)) + AirGlow(sunDot);
                float2 onSun = float2(dot(dir, _VoxSunRight.xyz), dot(dir, _VoxSunUp.xyz)) / max(sunDot, 1e-3);
                float edge = _VoxSun.w > 0.5 ? max(abs(onSun.x), abs(onSun.y)) : length(onSun);
                float aa = fwidth(edge);
                float disc = (1 - smoothstep(_VoxSun.x - aa, _VoxSun.x + aa, edge)) * step(0, sunDot);
                sky = lerp(sky, _VoxSunColor.rgb * 1.5, disc);

                float raw = SceneDepth(uv);
                float3 color;
                if (IsSky(raw) > 0.5)
                {
                    color = sky;
                }
                else
                {
                    // Fog toward the sun glows like the sky behind it
                    float depth = LinearEyeDepth(raw, _ZBufferParams);
                    float fog = _VoxFog.z * saturate((depth - _VoxFog.x) / max(_VoxFog.y - _VoxFog.x, 1e-3));
                    color = scene.rgb + AirGlow(sunDot) * fog;
                }

                if (_VoxSunScreen.z > 0) color += _VoxSunColor.rgb * GodRays(uv) * _VoxRays.x * _VoxSunScreen.z;
                return float4(color, scene.a);
            }
            ENDHLSL
        }
    }
}

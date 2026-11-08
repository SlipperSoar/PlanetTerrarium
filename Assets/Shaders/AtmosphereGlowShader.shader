Shader "Custom/AtmosphereGlowShader"
{
    /// <summary>
    /// 行星大气：球壳单次散射 raymarch + 深度遮挡（步骤 3）。
    ///
    /// 由 V3.2 的纯 Fresnel 光环升级而来，但**仍是同一个球壳**（不新增层），
    /// 球壳管理、缩放、渲染顺序全部复用 AtmosphereGlow.cs。
    ///
    /// 为什么改成 Cull Off：
    ///   相机在球壳外时只画正面即可；但「地表巡回」模式会让相机进入球壳内部，
    ///   此时 Cull Back 会让球壳整个消失。散射在球内/球外看是**不同**的积分路径，
    ///   必须两面都画、由 raymarch 显式求相机射线与球壳的交点区间。
    ///
    /// 深度遮挡：采样 _CameraDepthTexture，把视线碰到不透明地表的位置作为积分终点，
    /// 大气不会画到星球前面、山体能正确挡住大气。需相机开 DepthTextureMode.Depth。
    ///
    /// 渲染：Blend One One 加色（发光不遮挡）、ZWrite Off、Queue Transparent+10
    /// （在云层之后最后画）。
    /// </summary>
    Properties
    {
        _ScatterColor ("Scatter Color", Color) = (0.35, 0.62, 1.0, 1)
        _Density ("Atmosphere Density", Range(0.01, 4)) = 0.65
        _ScatterScale ("Scatter Scale", Range(0.2, 20)) = 3.0
        _RaySteps ("Raymarch Steps", Range(4, 32)) = 12
        _SunIntensity ("Sun Intensity", Range(0, 4)) = 1.6
        _Rayleigh ("Rayleigh Phase", Range(0, 2)) = 1.0
        _Mie ("Mie Phase", Range(0, 2)) = 0.25
        _Exposure ("Exposure", Range(0.1, 4)) = 1.0

        // V3.2 的 Fresnel 保留为边缘增强，可单独调
        _FresnelColor ("Edge Glow Color", Color) = (0.45, 0.72, 1.0, 1)
        _FresnelPower ("Edge Falloff", Range(0.5, 8)) = 2.2
        _FresnelStrength ("Edge Glow Strength", Range(0, 3)) = 0.5
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+10" "IgnoreProjector"="True" }
        LOD 100

        Pass
        {
            Blend One One
            ZWrite Off
            Cull Off              // 内外都要能看到球壳
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile _ _ATMOSPHERE_DEPTH_ON

            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos       : SV_POSITION;
                float3 worldPos  : TEXCOORD0;
                float3 worldNorm : TEXCOORD1;
                float4 screenPos : TEXCOORD2;
            };

            fixed4 _ScatterColor, _FresnelColor;
            float _Density, _ScatterScale, _SunIntensity, _Rayleigh, _Mie, _Exposure;
            float _FresnelPower, _FresnelStrength;
            int _RaySteps;

            // 球壳参数：由 AtmosphereGlow.cs 每帧写入（世界空间球心 + 内/外半径）
            float4 _AtmoSphere;        // xyz = 球心, w = 外半径
            float _AtmoInnerRadius;    // 内半径（星球半径）
            float _AtmoThickness;      // 厚度 = 外 - 内

            sampler2D _CameraDepthTexture;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldNorm = UnityObjectToWorldNormal(v.normal);
                o.screenPos = ComputeScreenPos(o.pos);
                return o;
            }

            // 射线与球（中心 c、半径 r）求交，返回 (近, 远)，无交返回 (-1,-1)
            float2 RaySphere(float3 ro, float3 rd, float3 c, float r)
            {
                float3 oc = ro - c;
                float b = dot(oc, rd);
                float cc = dot(oc, oc) - r * r;
                float h = b * b - cc;
                if (h < 0.0) return float2(-1.0, -1.0);
                h = sqrt(h);
                return float2(-b - h, -b + h);
            }

            // 高度 h ∈ [0,1]（0 = 地表 / 内壳，1 = 外壳顶）的密度，随高度指数衰减。
            // 地表最密(h01=0 → 1)、外壳最稀(h01=1 → exp(-4*density))。
            // 注意别写反成 exp(-(1-h01)*...)：那会让地表密度最小，球内贴地表
            // 观察时散射几乎为零（实测踩过的坑）。
            float DensityAt(float h01)
            {
                return exp(-max(0.0, h01) * 4.0 * _Density);
            }

            // 瑞利 / 米氏相位（cosθ = 视线与光方向夹角）
            float Phase(float cosTheta)
            {
                float rayleigh = 0.75 * (1.0 + cosTheta * cosTheta) * _Rayleigh;
                float g = 0.76;
                float mie = _Mie * (1.0 - g * g) /
                            pow(max(0.05, 1.0 + g * g - 2.0 * g * cosTheta), 1.5);
                return rayleigh + mie;
            }

            // 采样场景深度，换算成「视线方向上的世界距离」。
            // 防御：深度纹理未就绪时 SAMPLE_DEPTH_TEXTURE 可能返回 0，
            // LinearEyeDepth(0)=0 → 会把 tExit 钳成 0 → 整片散射消失。
            // 因此对「接近 0 的深度」视为无效，返回极大值（不遮挡）。
            float SceneDistance(float4 screenPos, float3 ro, float3 rd)
            {
#if defined(_ATMOSPHERE_DEPTH_ON)
                float2 uv = screenPos.xy / max(screenPos.w, 1e-5);
                float depth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
                if (depth <= 1e-5)          // 深度纹理无效 / 未清除
                    return 1e9;
                float linearEye = LinearEyeDepth(depth);
                // screenPos 是透视投影；linearEye 已是沿相机 forward 的世界距离。
                // rd 是归一化的世界视线，dot(rd, cameraForward) 修正斜视角。
                float3 camF = -UNITY_MATRIX_V[2].xyz;   // 相机 forward（世界）
                float cosA = max(0.05, abs(dot(rd, normalize(camF))));
                return linearEye / cosA;
#else
                return 1e9;   // 没开深度时：不遮挡
#endif
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 ro = _WorldSpaceCameraPos;
                float3 rd = normalize(i.worldPos - ro);
                float3 C = _AtmoSphere.xyz;
                float outerR = _AtmoSphere.w;
                float innerR = _AtmoInnerRadius;
                float thickness = max(1e-4, _AtmoThickness);

                // 射线与外壳的交点区间；相机可能在球内（near<0）也可能在球外
                float2 outerHit = RaySphere(ro, rd, C, outerR);
                if (outerHit.y < 0.0)
                    return fixed4(0, 0, 0, 1);   // 射线完全没穿过大气

                float tEnter = max(0.0, outerHit.x);   // 相机在球内时从相机起
                float tExit = outerHit.y;

                // 内壳（星球）遮挡：射线若穿过星球，积分在内壳前结束
                float2 innerHit = RaySphere(ro, rd, C, innerR);
                if (innerHit.x > 0.0)
                    tExit = min(tExit, innerHit.x);

                // 场景深度遮挡（山体/地表写进深度缓冲时更精确）
                tExit = min(tExit, SceneDistance(i.screenPos, ro, rd));
                if (tExit <= tEnter)
                    return fixed4(0, 0, 0, 1);

                float3 L = normalize(_WorldSpaceLightPos0.xyz);
                float cosTheta = dot(rd, L);

                // raymarch：沿 [tEnter, tExit] 采样，累积光学深度
                int steps = clamp(_RaySteps, 4, 32);
                float segLen = (tExit - tEnter) / steps;
                float opticalDepth = 0.0;
                float sunLit = 0.0;

                for (int s = 0; s < steps; s++)
                {
                    float t = tEnter + (s + 0.5) * segLen;
                    float3 p = ro + rd * t;
                    float r = length(p - C);
                    float h01 = saturate((r - innerR) / thickness);
                    float d = DensityAt(h01);
                    opticalDepth += d * segLen;

                    // 该采样点朝太阳方向的大气光学厚度（单次散射近似）
                    float2 sunHit = RaySphere(p, L, C, outerR);
                    float sunPath = max(0.0, sunHit.y - max(0.0, sunHit.x));
                    float sunDensity = DensityAt(saturate((r - innerR) / thickness));
                    float transmittance = exp(-sunDensity * sunPath * 0.5 / thickness);
                    sunLit += d * transmittance * segLen;
                }

                // 标定（Beer-Lambert + 软压缩）：
                //   - opticalDepth 是沿视线的真实积分，球外路径长、球内路径短，
                //     天然让球外更亮（符合物理）。
                //   - 用 x/(1+x) 软压缩而不是 1-exp(-x)：长路径平滑趋向 1 不会陡然
                //     饱和成一片死白，短路径也保留可见梯度。
                //   - _ScatterScale 调整体强度；_Density 已含在 DensityAt 里，这里
                //     不再重复乘，避免把球内短路径压到 2~7% 看不见。
                float tau = (opticalDepth + sunLit * 0.5) * Phase(cosTheta)
                            * _SunIntensity * _ScatterScale;
                float scatter = tau / (1.0 + tau);       // 软压缩到 [0,1)
                scatter *= _Exposure;
                scatter = saturate(scatter);

                float3 col = _ScatterColor.rgb * scatter;

                // V3.2 的 Fresnel 边缘增强（正对镜头处几乎为 0，轮廓最亮）
                float3 N = normalize(i.worldNorm);
                float3 V = normalize(ro - i.worldPos);
                float ndv = abs(dot(N, V));   // Cull Off：内外都取绝对值
                float rim = pow(saturate(1.0 - ndv), _FresnelPower);
                col += _FresnelColor.rgb * rim * _FresnelStrength;

                // Blend One One：alpha 不参与，写 1
                return fixed4(col, 1.0);
            }
            ENDCG
        }
    }
    FallBack Off
}

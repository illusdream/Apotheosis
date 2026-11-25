// x = enabled, y = strength, z = maxOffset, w = flipMaskY (离屏通道与最终场景垂直方向不同时翻转 UV.y)
float4 uDistortionParams;
// x = enabled, y = intensity, z = aspectInv (height / width), w = flipOffsetY (最终场景倒立时翻转屏幕偏移 Y 方向)
float4 uBloomParams;
// rgb = bloomTint, a = distortionChromaticScale (扭曲通道 B 的局部棱镜色散倍率)
float4 uBloomTint;
// x = enabled, y = exposureScale(2^ev), z = contrastScale, w = saturationScale
float4 uColorAdjustParams;
// rgb = colorFilter, a = hueShiftNormalized (-0.5..+0.5)
float4 uColorFilterHue;
// rgb = shadowsColor, a = enabled
float4 uSplitShadows;
// rgb = highlightsColor, a = balance (-1..+1)
float4 uSplitHighlights;

texture2D uImage0;
sampler2D uSampler0 : register(s0) = sampler_state
{
    Texture = <uImage0>;
};

texture2D uImage1;
sampler2D uSampler1 : register(s1) = sampler_state
{
    Texture = <uImage1>;
};

texture2D uImage2;
sampler2D uSampler2 : register(s2) = sampler_state
{
    Texture = <uImage2>;
};

struct VSInput
{
    float3 Position : POSITION0;
    float2 TexCoord : TEXCOORD0;
};

struct VSOutput
{
    float4 Position : POSITION0;
    float2 TexCoord : TEXCOORD0;
};

VSOutput VS_Fullscreen(VSInput input)
{
    VSOutput output;
    output.Position = float4(input.Position, 1.0);
    output.TexCoord = input.TexCoord;
    return output;
}

float3 RgbToHsv(float3 c)
{
    float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
    float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
    float d = q.x - min(q.w, q.y);
    float e = 1e-5;
    return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

float3 HsvToRgb(float3 c)
{
    float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
    return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
}

float4 PS_UberPost(VSOutput input) : COLOR0
{
    float2 uv = input.TexCoord;
    bool flipMaskY = uDistortionParams.w > 0.5;
    float2 finalOffset = float2(0.0, 0.0);
    float chromaAmount = 0.0;

    // 1. 扭曲偏移与 B 通道局部棱镜色散强度（同步作用于完整天空+世界与 Bloom 光晕）
    if (uDistortionParams.x > 0.5)
    {
        float2 distUv = flipMaskY ? float2(uv.x, 1.0 - uv.y) : uv;
        float4 distSample = tex2D(uSampler1, distUv);
        float2 offset = distSample.rg * uDistortionParams.y;
        if (uBloomParams.w > 0.5)
            offset.y = -offset.y;

        float len = length(offset);
        float maxOff = uDistortionParams.z;
        if (len > maxOff && len > 1e-6)
            offset *= (maxOff / len);
        offset.x *= uBloomParams.z;

        finalOffset = offset;
        uv = saturate(uv + offset);
        chromaAmount = clamp(max(distSample.b, 0.0) * uBloomTint.a, 0.0, 2.0);
    }

    // 2. 采样完整画面并叠加 Selective Bloom（仅 B 通道局部色散 > 0 时按偏移方向分通道采样，其余区域零额外开销）
    float3 color;
    if (chromaAmount > 1e-4)
    {
        float2 chromaShift = finalOffset * chromaAmount;
        float2 uvR = saturate(uv + chromaShift);
        float2 uvB = saturate(uv - chromaShift);

        color.r = tex2D(uSampler0, uvR).r;
        color.g = tex2D(uSampler0, uv).g;
        color.b = tex2D(uSampler0, uvB).b;

        if (uBloomParams.x > 0.5)
        {
            float2 bloomUvR = flipMaskY ? float2(uvR.x, 1.0 - uvR.y) : uvR;
            float2 bloomUvG = flipMaskY ? float2(uv.x, 1.0 - uv.y) : uv;
            float2 bloomUvB = flipMaskY ? float2(uvB.x, 1.0 - uvB.y) : uvB;
            float3 bloom;
            bloom.r = max(tex2D(uSampler2, bloomUvR).r, 0.0);
            bloom.g = max(tex2D(uSampler2, bloomUvG).g, 0.0);
            bloom.b = max(tex2D(uSampler2, bloomUvB).b, 0.0);
            color += bloom * uBloomTint.rgb * uBloomParams.y;
        }
    }
    else
    {
        color = tex2D(uSampler0, uv).rgb;
        if (uBloomParams.x > 0.5)
        {
            float2 bloomUv = flipMaskY ? float2(uv.x, 1.0 - uv.y) : uv;
            float3 bloom = max(tex2D(uSampler2, bloomUv).rgb, 0.0);
            color += bloom * uBloomTint.rgb * uBloomParams.y;
        }
    }

    // 3. Color Adjustments（曝光 / 对比度 / 滤镜染色 / 色相偏移 / 饱和度）
    if (uColorAdjustParams.x > 0.5)
    {
        color *= uColorAdjustParams.y;
        color = max((color - 0.5) * uColorAdjustParams.z + 0.5, 0.0);
        color *= uColorFilterHue.rgb;

        if (abs(uColorFilterHue.a) > 1e-4)
        {
            float3 hsv = RgbToHsv(max(color, 0.0));
            hsv.x = frac(hsv.x + uColorFilterHue.a);
            color = HsvToRgb(hsv);
        }

        float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
        color = max(lerp(luma.xxx, color, uColorAdjustParams.w), 0.0);
    }

    // 4. Split Toning（分离色调：Pegtop SoftLight，0.5 中性灰恒等不变）
    if (uSplitShadows.a > 0.5)
    {
        float3 baseColor = saturate(color);
        float luma = dot(baseColor, float3(0.2126, 0.7152, 0.0722));
        float t = saturate(luma + uSplitHighlights.a * 0.5);
        float3 tone = lerp(uSplitShadows.rgb, uSplitHighlights.rgb, t);
        color = (1.0 - 2.0 * tone) * baseColor * baseColor + 2.0 * tone * baseColor;
    }

    return float4(saturate(color), 1.0);
}

technique UberPostTech
{
    pass DefaultPass
    {
        VertexShader = compile vs_3_0 VS_Fullscreen();
        PixelShader = compile ps_3_0 PS_UberPost();
    }
}

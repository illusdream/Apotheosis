float2 uTexelSize;
float uScatter;
float uClampMax;

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

float3 SafeClamp(float3 c)
{
    return clamp(max(c, 0.0), 0.0, uClampMax);
}

float KarisWeight(float3 c)
{
    float luma = max(c.r, max(c.g, c.b));
    return 1.0 / (1.0 + luma);
}

// 13-tap CoD:AW 首级降采样 + Karis 加权平均，抑制细线与高频光点闪烁
float4 PS_DownsampleFirst(VSOutput input) : COLOR0
{
    float2 uv = input.TexCoord;
    float x = uTexelSize.x;
    float y = uTexelSize.y;

    float3 a = SafeClamp(tex2D(uSampler0, uv + float2(-2.0 * x,  2.0 * y)).rgb);
    float3 b = SafeClamp(tex2D(uSampler0, uv + float2( 0.0,      2.0 * y)).rgb);
    float3 c = SafeClamp(tex2D(uSampler0, uv + float2( 2.0 * x,  2.0 * y)).rgb);

    float3 d = SafeClamp(tex2D(uSampler0, uv + float2(-2.0 * x,  0.0)).rgb);
    float3 e = SafeClamp(tex2D(uSampler0, uv).rgb);
    float3 f = SafeClamp(tex2D(uSampler0, uv + float2( 2.0 * x,  0.0)).rgb);

    float3 g = SafeClamp(tex2D(uSampler0, uv + float2(-2.0 * x, -2.0 * y)).rgb);
    float3 h = SafeClamp(tex2D(uSampler0, uv + float2( 0.0,     -2.0 * y)).rgb);
    float3 i = SafeClamp(tex2D(uSampler0, uv + float2( 2.0 * x, -2.0 * y)).rgb);

    float3 j = SafeClamp(tex2D(uSampler0, uv + float2(-x,  y)).rgb);
    float3 k = SafeClamp(tex2D(uSampler0, uv + float2( x,  y)).rgb);
    float3 l = SafeClamp(tex2D(uSampler0, uv + float2(-x, -y)).rgb);
    float3 m = SafeClamp(tex2D(uSampler0, uv + float2( x, -y)).rgb);

    float3 g0 = (a + b + d + e) * 0.25;
    float3 g1 = (b + c + e + f) * 0.25;
    float3 g2 = (d + e + g + h) * 0.25;
    float3 g3 = (e + f + h + i) * 0.25;
    float3 g4 = (j + k + l + m) * 0.25;

    float w0 = KarisWeight(g0) * 0.125;
    float w1 = KarisWeight(g1) * 0.125;
    float w2 = KarisWeight(g2) * 0.125;
    float w3 = KarisWeight(g3) * 0.125;
    float w4 = KarisWeight(g4) * 0.5;

    float3 sum = g0 * w0 + g1 * w1 + g2 * w2 + g3 * w3 + g4 * w4;
    float weightSum = max(w0 + w1 + w2 + w3 + w4, 1e-5);
    return float4(sum / weightSum, 1.0);
}

// 13-tap 常规金字塔降采样
float4 PS_Downsample(VSOutput input) : COLOR0
{
    float2 uv = input.TexCoord;
    float x = uTexelSize.x;
    float y = uTexelSize.y;

    float3 a = tex2D(uSampler0, uv + float2(-2.0 * x,  2.0 * y)).rgb;
    float3 b = tex2D(uSampler0, uv + float2( 0.0,      2.0 * y)).rgb;
    float3 c = tex2D(uSampler0, uv + float2( 2.0 * x,  2.0 * y)).rgb;

    float3 d = tex2D(uSampler0, uv + float2(-2.0 * x,  0.0)).rgb;
    float3 e = tex2D(uSampler0, uv).rgb;
    float3 f = tex2D(uSampler0, uv + float2( 2.0 * x,  0.0)).rgb;

    float3 g = tex2D(uSampler0, uv + float2(-2.0 * x, -2.0 * y)).rgb;
    float3 h = tex2D(uSampler0, uv + float2( 0.0,     -2.0 * y)).rgb;
    float3 i = tex2D(uSampler0, uv + float2( 2.0 * x, -2.0 * y)).rgb;

    float3 j = tex2D(uSampler0, uv + float2(-x,  y)).rgb;
    float3 k = tex2D(uSampler0, uv + float2( x,  y)).rgb;
    float3 l = tex2D(uSampler0, uv + float2(-x, -y)).rgb;
    float3 m = tex2D(uSampler0, uv + float2( x, -y)).rgb;

    float3 color = e * 0.125;
    color += (a + c + g + i) * 0.03125;
    color += (b + d + f + h) * 0.0625;
    color += (j + k + l + m) * 0.125;
    return float4(max(color, 0.0), 1.0);
}

// 9-tap Tent 滤波上采样并与上级 Mip 做能量守恒 Scatter 混合
float4 PS_Upsample(VSOutput input) : COLOR0
{
    float2 uv = input.TexCoord;
    float x = uTexelSize.x;
    float y = uTexelSize.y;

    float3 s0 = tex2D(uSampler0, uv + float2(-x,  y)).rgb;
    float3 s1 = tex2D(uSampler0, uv + float2( 0,  y)).rgb;
    float3 s2 = tex2D(uSampler0, uv + float2( x,  y)).rgb;

    float3 s3 = tex2D(uSampler0, uv + float2(-x,  0)).rgb;
    float3 s4 = tex2D(uSampler0, uv).rgb;
    float3 s5 = tex2D(uSampler0, uv + float2( x,  0)).rgb;

    float3 s6 = tex2D(uSampler0, uv + float2(-x, -y)).rgb;
    float3 s7 = tex2D(uSampler0, uv + float2( 0, -y)).rgb;
    float3 s8 = tex2D(uSampler0, uv + float2( x, -y)).rgb;

    float3 lowMip = (s4 * 4.0 + (s1 + s3 + s5 + s7) * 2.0 + (s0 + s2 + s6 + s8)) * (1.0 / 16.0);
    float3 highMip = tex2D(uSampler1, uv).rgb;
    float3 blended = lerp(highMip, lowMip, uScatter);
    return float4(max(blended, 0.0), 1.0);
}

technique DownsampleFirstTech
{
    pass DefaultPass
    {
        VertexShader = compile vs_3_0 VS_Fullscreen();
        PixelShader = compile ps_3_0 PS_DownsampleFirst();
    }
}

technique DownsampleTech
{
    pass DefaultPass
    {
        VertexShader = compile vs_3_0 VS_Fullscreen();
        PixelShader = compile ps_3_0 PS_Downsample();
    }
}

technique UpsampleTech
{
    pass DefaultPass
    {
        VertexShader = compile vs_3_0 VS_Fullscreen();
        PixelShader = compile ps_3_0 PS_Upsample();
    }
}

float4x4 uTransformMatrix;
float uDistortionStrength;
float uChromaticStrength;
float uInnerRadius;
float uOuterRadius;
float uEdgeSoftness;

texture2D uImage0;
sampler2D uSampler0 : register(s0) = sampler_state
{
    Texture = <uImage0>;
};

struct VSInput
{
    float3 Position : POSITION0;
    float3 Normal   : NORMAL0;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
    float4 Custom   : TEXCOORD1;
};

struct VSOutput
{
    float4 Position : POSITION0;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

VSOutput VS_Standard(VSInput input)
{
    VSOutput output;
    output.Position = mul(float4(input.Position, 1.0), uTransformMatrix);
    output.Color = input.Color;
    output.TexCoord = input.TexCoord;
    return output;
}

// 法线/流向贴图解码写入 FP16 扭曲通道（RG = 有符号偏移，B = 局部色散强度，A = 预留 0）
float4 PS_NormalMap(VSOutput input) : COLOR0
{
    float4 sampleVal = tex2D(uSampler0, input.TexCoord);
    float2 offset = (sampleVal.rg * 2.0 - sampleVal.a) * (input.Color.a * uDistortionStrength);
    float chroma = (sampleVal.a * input.Color.a) * uChromaticStrength;
    return float4(offset.x, offset.y, chroma, 0.0);
}

// 程序化径向环形冲击波写入 FP16 扭曲通道（RG = 有符号径向偏移，B = 局部色散强度，A = 预留 0）
float4 PS_RadialRing(VSOutput input) : COLOR0
{
    float2 delta = input.TexCoord - 0.5;
    float dist = length(delta);
    float2 dir = dist > 1e-4 ? (delta / dist) : float2(0.0, 0.0);

    float soft = max(uEdgeSoftness, 1e-4);
    float inner = smoothstep(uInnerRadius - soft, uInnerRadius + soft, dist);
    float outer = 1.0 - smoothstep(uOuterRadius - soft, uOuterRadius + soft, dist);
    float mask = inner * outer * input.Color.a;

    float2 offset = dir * (mask * uDistortionStrength);
    float chroma = mask * uChromaticStrength;
    return float4(offset.x, offset.y, chroma, 0.0);
}

technique NormalMapTech
{
    pass DefaultPass
    {
        VertexShader = compile vs_3_0 VS_Standard();
        PixelShader = compile ps_3_0 PS_NormalMap();
    }
}

technique RadialRingTech
{
    pass DefaultPass
    {
        VertexShader = compile vs_3_0 VS_Standard();
        PixelShader = compile ps_3_0 PS_RadialRing();
    }
}

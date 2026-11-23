float4x4 uTransformMatrix;

texture2D uImage0;
sampler2D uSampler0 : register(s0) = sampler_state
{
    Texture = <uImage0>;
};

struct VSStandardInput
{
    float3 Position : POSITION0;
    float3 Normal   : NORMAL0;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
    float4 Custom   : TEXCOORD1;
};

struct VSInstancedInput
{
    // Stream 0: UnifiedVertex
    float3 Position      : POSITION0;
    float3 Normal        : NORMAL0;
    float4 Color         : COLOR0;
    float2 TexCoord      : TEXCOORD0;
    float4 Custom        : TEXCOORD1;

    // Stream 1: InstanceData (3x4 XNA row-vector affine columns + Color + UVRect/Custom)
    float4 TransformCol0 : TEXCOORD2;
    float4 TransformCol1 : TEXCOORD3;
    float4 TransformCol2 : TEXCOORD4;
    float4 InstColor     : TEXCOORD5;
    float4 InstCustom    : TEXCOORD6;
};

struct VSOutput
{
    float4 Position : POSITION0;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

VSOutput VS_Standard(VSStandardInput input)
{
    VSOutput output;
    output.Position = mul(float4(input.Position, 1.0), uTransformMatrix);
    output.Color = input.Color;
    output.TexCoord = input.TexCoord;
    return output;
}

VSOutput VS_Instanced(VSInstancedInput input)
{
    float4 localPos = float4(input.Position, 1.0);
    float3 worldPos = float3(
        dot(localPos, input.TransformCol0),
        dot(localPos, input.TransformCol1),
        dot(localPos, input.TransformCol2));

    VSOutput output;
    output.Position = mul(float4(worldPos, 1.0), uTransformMatrix);
    output.Color = input.Color * input.InstColor;
    output.TexCoord = input.InstCustom.xy + input.TexCoord * input.InstCustom.zw;
    return output;
}

float4 PS_Default(VSOutput input) : COLOR0
{
    return tex2D(uSampler0, input.TexCoord) * input.Color;
}

technique StandardTech
{
    pass DefaultPass
    {
        VertexShader = compile vs_3_0 VS_Standard();
        PixelShader = compile ps_3_0 PS_Default();
    }
}

technique InstancedTech
{
    pass DefaultPass
    {
        VertexShader = compile vs_3_0 VS_Instanced();
        PixelShader = compile ps_3_0 PS_Default();
    }
}

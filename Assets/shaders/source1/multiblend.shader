HEADER
{
	Description = "Source 1 four-layer displacement terrain";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	Depth( S_MODE_DEPTH );
	ToolsShadingComplexity( "tools_shading_complexity.shader" );
}

COMMON
{
	#define CUSTOM_MATERIAL_INPUTS
	StaticCombo( S_RENDER_BACKFACES, F_RENDER_BACKFACES, Sys( ALL ) );
	#include "common/shared.hlsl"
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
	float4 vBlendAlpha : TEXCOORD3 < Semantic( TexCoord3 ); >;
	float4 vBlendColor1 : TEXCOORD4 < Semantic( TexCoord4 ); >;
	float4 vBlendColor2 : TEXCOORD5 < Semantic( TexCoord5 ); >;
	float4 vBlendColor3 : TEXCOORD6 < Semantic( TexCoord6 ); >;
	float4 vBlendColor4 : TEXCOORD7 < Semantic( TexCoord7 ); >;
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
	float4 vBlendAlpha : TEXCOORD15;
	float4 vBlendColor1 : TEXCOORD16;
	float4 vBlendColor2 : TEXCOORD17;
	float4 vBlendColor3 : TEXCOORD18;
	float4 vBlendColor4 : TEXCOORD19;
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput v )
	{
		PixelInput i = ProcessVertex( v );
		i.vBlendAlpha = v.vBlendAlpha;
		i.vBlendColor1 = v.vBlendColor1;
		i.vBlendColor2 = v.vBlendColor2;
		i.vBlendColor3 = v.vBlendColor3;
		i.vBlendColor4 = v.vBlendColor4;
		return FinalizeVertex( i );
	}
}

PS
{
	#include "common/pixel.hlsl"

	SamplerState g_sLayer1 < Attribute( "g_tLayer1Sampler" ); Filter( ANISO ); AddressU( WRAP ); AddressV( WRAP ); >;
	SamplerState g_sSpec1 < Attribute( "g_tSpec1Sampler" ); Filter( ANISO ); AddressU( WRAP ); AddressV( WRAP ); >;
	CreateInputTexture2D( Layer1, Srgb, 8, "None", "_color", "Terrain", Default4( 1, 1, 1, 1 ) );
	CreateInputTexture2D( Spec1, Srgb, 8, "None", "_mask", "Terrain", Default4( 0, 0, 0, 1 ) );
	Texture2D g_tLayer1 < Channel( RGBA, Box( Layer1 ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	Texture2D g_tSpec1 < Channel( RGBA, Box( Spec1 ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	float3 g_vLayerTransform1 < Default3( 1, 0, 1 ); >;

	SamplerState g_sLayer2 < Attribute( "g_tLayer2Sampler" ); Filter( ANISO ); AddressU( WRAP ); AddressV( WRAP ); >;
	SamplerState g_sSpec2 < Attribute( "g_tSpec2Sampler" ); Filter( ANISO ); AddressU( WRAP ); AddressV( WRAP ); >;
	CreateInputTexture2D( Layer2, Srgb, 8, "None", "_color", "Terrain", Default4( 1, 1, 1, 1 ) );
	CreateInputTexture2D( Spec2, Srgb, 8, "None", "_mask", "Terrain", Default4( 0, 0, 0, 1 ) );
	Texture2D g_tLayer2 < Channel( RGBA, Box( Layer2 ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	Texture2D g_tSpec2 < Channel( RGBA, Box( Spec2 ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	float3 g_vLayerTransform2 < Default3( 1, 0, 1 ); >;

	SamplerState g_sLayer3 < Attribute( "g_tLayer3Sampler" ); Filter( ANISO ); AddressU( WRAP ); AddressV( WRAP ); >;
	SamplerState g_sSpec3 < Attribute( "g_tSpec3Sampler" ); Filter( ANISO ); AddressU( WRAP ); AddressV( WRAP ); >;
	CreateInputTexture2D( Layer3, Srgb, 8, "None", "_color", "Terrain", Default4( 1, 1, 1, 1 ) );
	CreateInputTexture2D( Spec3, Srgb, 8, "None", "_mask", "Terrain", Default4( 0, 0, 0, 1 ) );
	Texture2D g_tLayer3 < Channel( RGBA, Box( Layer3 ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	Texture2D g_tSpec3 < Channel( RGBA, Box( Spec3 ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	float3 g_vLayerTransform3 < Default3( 1, 0, 1 ); >;

	SamplerState g_sLayer4 < Attribute( "g_tLayer4Sampler" ); Filter( ANISO ); AddressU( WRAP ); AddressV( WRAP ); >;
	SamplerState g_sSpec4 < Attribute( "g_tSpec4Sampler" ); Filter( ANISO ); AddressU( WRAP ); AddressV( WRAP ); >;
	CreateInputTexture2D( Layer4, Srgb, 8, "None", "_color", "Terrain", Default4( 1, 1, 1, 1 ) );
	CreateInputTexture2D( Spec4, Srgb, 8, "None", "_mask", "Terrain", Default4( 0, 0, 0, 1 ) );
	Texture2D g_tLayer4 < Channel( RGBA, Box( Layer4 ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	Texture2D g_tSpec4 < Channel( RGBA, Box( Spec4 ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;
	float3 g_vLayerTransform4 < Default3( 1, 0, 1 ); >;

	float2 LayerUv( float2 uv, float3 transform )
	{
		float2 centered = uv - 0.5;
		return float2( centered.x * transform.x - centered.y * transform.y, centered.x * transform.y + centered.y * transform.x ) / transform.z + 0.5;
	}

	float BlendFactor( float start, float end, float amount, float alpha, inout float remaining )
	{
		float result = 0;
		if ( remaining > 0 && amount > 0 )
		{
			float minimum = max( 0, end - start );
			float maximum = min( 1, end + start );
			result = minimum != maximum ? smoothstep( minimum, maximum, amount ) : step( minimum, amount );
			if ( end < alpha )
				result *= saturate( 2 - alpha - end );
		}
		result = clamp( result, 0, remaining );
		remaining -= result;
		return result;
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 uv1 = LayerUv( i.vTextureCoords.xy, g_vLayerTransform1 );
		float4 color1 = g_tLayer1.Sample( g_sLayer1, uv1 ) * float4( i.vBlendColor1.rgb, 1 );
		float4 spec1 = g_tSpec1.Sample( g_sSpec1, uv1 );
		float2 uv2 = LayerUv( i.vTextureCoords.xy, g_vLayerTransform2 );
		float4 color2 = g_tLayer2.Sample( g_sLayer2, uv2 ) * float4( i.vBlendColor2.rgb, 1 );
		float4 spec2 = g_tSpec2.Sample( g_sSpec2, uv2 );
		float2 uv3 = LayerUv( i.vTextureCoords.xy, g_vLayerTransform3 );
		float4 color3 = g_tLayer3.Sample( g_sLayer3, uv3 ) * float4( i.vBlendColor3.rgb, 1 );
		float4 spec3 = g_tSpec3.Sample( g_sSpec3, uv3 );
		float2 uv4 = LayerUv( i.vTextureCoords.xy, g_vLayerTransform4 );
		float4 color4 = g_tLayer4.Sample( g_sLayer4, uv4 ) * float4( i.vBlendColor4.rgb, 1 );
		float4 spec4 = g_tSpec4.Sample( g_sSpec4, uv4 );
		float remaining = 1;
		float blend1 = BlendFactor( spec1.a, color1.a, i.vBlendColor1.a, i.vBlendAlpha.x, remaining );
		float blend2 = BlendFactor( spec2.a, color2.a, i.vBlendColor2.a, i.vBlendAlpha.y, remaining );
		float blend3 = BlendFactor( spec3.a, color3.a, i.vBlendColor3.a, i.vBlendAlpha.z, remaining );
		float3 albedo = color1.rgb * blend1 + color2.rgb * blend2 + color3.rgb * blend3 + color4.rgb * remaining;
		float3 specular = spec1.rgb * blend1 + spec2.rgb * blend2 + spec3.rgb * blend3 + spec4.rgb * remaining;
		Material material = Material::Init( i.vPositionWithOffsetWs, i.vPositionSs );
		material.Albedo = albedo;
		material.Normal = normalize( i.vNormalWs );
		material.TangentNormal = float3( 0, 0, 1 );
		material.WorldTangentU = i.vTangentUWs;
		material.WorldTangentV = i.vTangentVWs;
		material.TextureCoords = uv1;
		material.Opacity = 1;
		material.Roughness = 0.8;
		#if S_MODE_DEPTH
			if ( DepthNormals::WantsDepthNormals() )
				return DepthNormals::Output( material.Normal, material.Roughness, 1 );
			return float4( 0, 0, 0, 1 );
		#else
			Decals::Apply( material.WorldPosition, material );
			CombinerInput combiner = ShadingModelStandard::MaterialToCombinerInput( material );
			combiner.vSpecularColor = saturate( specular * 0.04 );
			LightingTerms_t lighting = InitLightingTerms();
			ComputeDirectLighting( lighting, combiner );
			CalculateIndirectLighting( lighting, combiner );
			float3 result = (lighting.vDiffuse + lighting.vIndirectDiffuse) * combiner.vDiffuseColor
				+ lighting.vSpecular + lighting.vIndirectSpecular + combiner.vEmissive;
			if ( g_bFogEnabled )
				result = Fog::Apply( material.WorldPosition, material.ScreenPosition.xy, result );
			return float4( result, 1 );
		#endif
	}
}

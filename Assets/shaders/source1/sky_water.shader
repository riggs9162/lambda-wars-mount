HEADER
{
	Description = "Source 1 sky water";
}

FEATURES
{
	#include "common/features.hlsl"
	Feature( F_SOURCE_TEXTURE_ANIMATION, 0..1, "Source 1" );
	Feature( F_SOURCE_CUBEMAP, 0..1, "Source 1" );
}

MODES
{
	Forward();
}

COMMON
{
	#define CUSTOM_MATERIAL_INPUTS
	#include "common/shared.hlsl"
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
	nointerpolation float flSourceFrame : TEXCOORD16;
};

VS
{
	#include "common/vertex.hlsl"
	#include "source1/frames.hlsl"

	PixelInput MainVs( VertexInput v )
	{
		PixelInput i = ProcessVertex( v );
		i.flSourceFrame = SourceFrame();
		return FinalizeVertex( i );
	}
}

PS
{
	StaticCombo( S_SOURCE_CUBEMAP, F_SOURCE_CUBEMAP, Sys( ALL ) );
	#include "common/pixel.hlsl"
	#include "source1/frames.hlsl"
	#include "source1/uv.hlsl"

	SamplerState g_sNormal < Attribute( "g_tNormalSampler" ); Filter( ANISO ); AddressU( WRAP ); AddressV( WRAP ); >;
	SamplerState g_sEnvCube < Attribute( "g_tEnvCubeSampler" ); Filter( TRILINEAR ); AddressU( CLAMP ); AddressV( CLAMP ); AddressW( CLAMP ); >;
	CreateInputTexture2D( SourceNormal, Linear, 8, "None", "_normal", "Source 1", Default4( 0.5, 0.5, 1, 1 ) );
	CreateInputTextureCube( SourceEnvCube, Srgb, 8, "None", "_cube", "Source 1", Default4( 0, 0, 0, 1 ) );
	#if S_SOURCE_TEXTURE_ANIMATION
		Texture2DArray g_tNormal < Channel( RGBA, Box( SourceNormal ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	#else
		Texture2D g_tNormal < Channel( RGBA, Box( SourceNormal ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	#endif
	CreateTextureCube( g_tEnvCube ) < Channel( RGBA, Box( SourceEnvCube ), Srgb ); OutputFormat( RGBA8888 ); SrgbRead( true ); >;

	float3 g_vSourceNormalTransformU < Default3( 1, 0, 0 ); >;
	float3 g_vSourceNormalTransformV < Default3( 0, 1, 0 ); >;
	float3 g_vSourceNormalScroll < Default3( 0, 0, 1 ); >;
	float2 g_vSourceWaterScale < Default2( 1, 1 ); >;
	float2 g_vSourceWaterScroll1 < Default2( 0, 0 ); >;
	float2 g_vSourceWaterScroll2 < Default2( 0, 0 ); >;
	float3 g_vSourceFogColor < Default3( 0, 0, 0 ); >;
	float3 g_vSourceReflectTint < Default3( 1, 1, 1 ); >;
	int g_nSourceNormalEncoding < Default( 0 ); >;

	float3 SourceWaterNormal( float2 uv, float frame )
	{
		float3 normal = g_tNormal.Sample( g_sNormal, SourceFrameUv( uv, frame ) ).xyz * 2 - 1;
		if ( g_nSourceNormalEncoding == 1 )
			normal.z = sqrt( saturate( 1 - dot( normal.xy, normal.xy ) ) );
		return normal;
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float3 positionWs = i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz;
		float3 view = normalize( g_vCameraPositionWs.xyz - positionWs );
		float2 uv = SourceUv( i.vTextureCoords.xy, g_vSourceNormalTransformU, g_vSourceNormalTransformV, g_vSourceNormalScroll ) * g_vSourceWaterScale;
		float3 normal1 = SourceWaterNormal( uv + frac( g_vSourceWaterScroll1 * g_flTime ), i.flSourceFrame );
		float3 normal2 = SourceWaterNormal( uv * 1.37 + frac( g_vSourceWaterScroll2 * g_flTime ), i.flSourceFrame );
		float3 normalWs = normalize( TransformNormal( normalize( normal1 + normal2 ), i.vNormalWs, i.vTangentUWs, i.vTangentVWs ) );
		float3 result = g_vSourceFogColor;
		#if S_SOURCE_CUBEMAP
			float fresnel = pow( 1 - saturate( dot( view, normalWs ) ), 5 );
			float3 reflected = g_tEnvCube.Sample( g_sEnvCube, reflect( -view, normalWs ) ).rgb * g_vSourceReflectTint;
			result = lerp( result, reflected, fresnel );
		#endif
		return DoAtmospherics( positionWs, i.vPositionSs.xy, float4( result, 1 ), false );
	}
}

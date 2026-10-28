HEADER
{
	Description = "Source 1 water";
}

FEATURES
{
	#include "common/features.hlsl"
	Feature( F_SOURCE_ABOVE_WATER, 0..1, "Source 1" );
	Feature( F_SOURCE_TEXTURE_ANIMATION, 0..1, "Source 1" );
}

MODES
{
	Forward();
}

COMMON
{
	#define S_TRANSLUCENT 1
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
	StaticCombo( S_SOURCE_ABOVE_WATER, F_SOURCE_ABOVE_WATER, Sys( ALL ) );
	#define DEPTH_STATE_ALREADY_SET 1
	#define BLEND_MODE_ALREADY_SET 1
	#define CULL_MODE_ALREADY_SET 1
	#include "common/pixel.hlsl"
	#include "common/classes/Depth.hlsl"
	#include "source1/frames.hlsl"
	#include "source1/uv.hlsl"

	BoolAttribute( bWantsFBCopyTexture, true );
	BoolAttribute( renderbackfaces, F_RENDER_BACKFACES != 0 );
	RenderState( DepthEnable, true );
	RenderState( DepthFunc, GREATER_EQUAL );
	RenderState( DepthWriteEnable, false );
	RenderState( BlendEnable, true );
	RenderState( SrcBlend, SRC_ALPHA );
	RenderState( DstBlend, INV_SRC_ALPHA );

	SamplerState g_sNormal < Attribute( "g_tNormalSampler" ); Filter( ANISO ); AddressU( WRAP ); AddressV( WRAP ); >;
	SamplerState g_sClamp < Filter( TRILINEAR ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	Texture2D g_tSourceScene < Attribute( "FrameBufferCopyTexture" ); SrgbRead( false ); >;

	CreateInputTexture2D( SourceNormal, Linear, 8, "None", "_normal", "Source 1", Default4( 0.5, 0.5, 1, 1 ) );
	#if S_SOURCE_TEXTURE_ANIMATION
		Texture2DArray g_tNormal < Channel( RGBA, Box( SourceNormal ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	#else
		Texture2D g_tNormal < Channel( RGBA, Box( SourceNormal ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
	#endif

	float3 g_vSourceNormalTransformU < Default3( 1, 0, 0 ); >;
	float3 g_vSourceNormalTransformV < Default3( 0, 1, 0 ); >;
	float3 g_vSourceNormalScroll < Default3( 0, 0, 1 ); >;
	float2 g_vSourceWaterScale < Default2( 1, 1 ); >;
	float2 g_vSourceWaterScroll1 < Default2( 0, 0 ); >;
	float2 g_vSourceWaterScroll2 < Default2( 0, 0 ); >;
	float g_flSourceRefractAmount < Default( 0.5 ); >;
	float g_flSourceReflectAmount < Default( 0.5 ); >;
	float3 g_vSourceRefractTint < Default3( 1, 1, 1 ); >;
	float3 g_vSourceReflectTint < Default3( 1, 1, 1 ); >;
	float g_flSourceFogEnable < Default( 0 ); >;
	float3 g_vSourceFogColor < Default3( 0, 0, 0 ); >;
	float2 g_vSourceFogRange < Default2( 0, 100 ); >;
	int g_nSourceNormalEncoding < Default( 0 ); >;

	float3 SourceScene( float2 uv )
	{
		return g_tSourceScene.SampleLevel( g_sClamp, saturate( uv ) * g_vFrameBufferCopyInvSizeAndUvScale.zw, 0 ).rgb;
	}

	float3 SourceWaterNormal( float2 uv, float frame )
	{
		float3 normal = g_tNormal.Sample( g_sNormal, SourceFrameUv( uv, frame ) ).xyz * 2 - 1;
		if ( g_nSourceNormalEncoding == 1 )
			normal.z = sqrt( saturate( 1 - dot( normal.xy, normal.xy ) ) );
		return normal;
	}

	// Vertical distance from the water surface down to the opaque scene behind it.
	float SourceWaterDepth( PixelInput i )
	{
		float3 ray = normalize( i.vPositionWithOffsetWs.xyz );
		float depth = 1.0f - Depth::GetNormalized( i.vPositionSs.xy );
		float3 scene = RecoverWorldPosFromProjectedDepthAndRay( depth, ray ) - g_vCameraPositionWs;
		return max( i.vPositionWithOffsetWs.z - scene.z, 0 );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float3 positionWs = i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz;
		float3 view = normalize( g_vCameraPositionWs.xyz - positionWs );
		float2 uv = SourceUv( i.vTextureCoords.xy, g_vSourceNormalTransformU, g_vSourceNormalTransformV, g_vSourceNormalScroll ) * g_vSourceWaterScale;
		float3 normal1 = SourceWaterNormal( uv + frac( g_vSourceWaterScroll1 * g_flTime ), i.flSourceFrame );
		float3 normal2 = SourceWaterNormal( uv * 1.37 + frac( g_vSourceWaterScroll2 * g_flTime ), i.flSourceFrame );
		float3 normalTs = normalize( normal1 + normal2 );

		float2 screenUv = CalculateViewportUv( i.vPositionSs.xy );
		float2 refractUv = screenUv + normalTs.xy * g_flSourceRefractAmount * 0.05;
		float3 refracted = SourceScene( refractUv );
		float refractDepth = SourceWaterDepth( i );
		refracted *= g_vSourceRefractTint;

		if ( g_flSourceFogEnable > 0.5 )
		{
			float fog = saturate( (refractDepth - g_vSourceFogRange.x) / max( g_vSourceFogRange.y - g_vSourceFogRange.x, 0.001 ) );
			refracted = lerp( refracted, g_vSourceFogColor, fog );
		}

		float3 result = refracted;
		#if S_SOURCE_ABOVE_WATER
		{
			float3 normalWs = TransformNormal( normalTs, i.vNormalWs, i.vTangentUWs, i.vTangentVWs );
			float fresnel = lerp( 0.02, 1, pow( 1 - saturate( dot( view, normalize( i.vNormalWs ) ) ), 5 ) );
			float3 reflected = EnvMap::From( positionWs, i.vPositionSs, normalWs, float2( 0.02, 0.02 ) ) * g_vSourceReflectTint;
			result = lerp( refracted, reflected, saturate( fresnel * g_flSourceReflectAmount * 2 ) );
		}
		#endif
		if ( g_bFogEnabled )
			result = Fog::Apply( positionWs, i.vPositionSs.xy, result );
		return float4( result, 1 );
	}
}

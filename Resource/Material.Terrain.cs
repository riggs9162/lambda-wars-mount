using System;
using System.IO;

namespace Sandbox.Mounting.Source1;

internal sealed partial class SourceMaterialBuilder
{
	private Material CreateTerrain( string resourceName )
	{
		var secondPath = parameters.String( "$basetexture2" );
		if ( string.IsNullOrWhiteSpace( secondPath ) )
			throw new InvalidDataException( $"{FullPath}: WorldVertexTransition requires $basetexture2." );

		var second = LoadSurfaceTexture( secondPath, parameters.Integer( "$frame2" ) );
		if ( second.IsCube )
			throw new InvalidDataException( $"{FullPath}: $basetexture2 requires a 2D texture." );

		var material = CreateSurface( "lightmappedgeneric", parameters, transforms, alpha, animation, resourceName );
		second.Bind( material, "g_tTexture2" );
		var normalPath = parameters.String( "$bumpmap2" );
		if ( !string.IsNullOrWhiteSpace( normalPath ) )
		{
			var normal = LoadSurfaceTexture( normalPath, parameters.Integer( "$bumpframe2" ) );
			if ( normal.IsCube )
				throw new InvalidDataException( $"{FullPath}: $bumpmap2 requires a 2D texture." );
			normal.Bind( material, "g_tTerrainNormal2" );
			material.Set( "g_flSourceTerrainNormal2", 1.0f );
			material.Set( "g_nSourceTerrainNormalEncoding2", normal.Header.HiResFormat == VtfFormat.UV88 ? 1 : 0 );
			material.Set( "g_flSourceTerrainSsBump2", parameters.Boolean( "$ssbump" ) || (normal.Header.Flags & VtfFlags.SSBUMP) != 0 );
		}
		var modulationPath = parameters.String( "$blendmodulatetexture" );
		if ( !string.IsNullOrWhiteSpace( modulationPath ) )
		{
			var modulation = LoadSurfaceTexture( modulationPath );
			if ( modulation.IsCube )
				throw new InvalidDataException( $"{FullPath}: $blendmodulatetexture requires a 2D texture." );
			modulation.Bind( material, "g_tTerrainModulation" );
			material.Set( "g_flSourceTerrainModulation", 1.0f );
		}
		transforms.Get( "$blendmasktransform" ).Bind( material, "TerrainMask" );
		material.Set( "g_flSourceTerrainMasked", parameters.Boolean( "$maskedblending" ) );
		transforms.Get( "$basetexturetransform2", transforms.Get( "$basetexturetransform" ) ).Bind( material, "Texture2" );
		material.SetFeature( "F_SOURCE_TERRAIN_BLEND", 1 );
		material.Set( "g_flSourceVertexAlpha", 0.0f );
		material.Set( "g_flSourceUseBaseAlpha", 0.0f );
		return material;
	}
}

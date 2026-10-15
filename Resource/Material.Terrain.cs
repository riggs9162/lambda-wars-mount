using System;
using System.IO;

namespace Sandbox.Mounting.Source1;

internal sealed partial class SourceMaterialBuilder
{
	private Material CreateTerrain( string resourceName )
	{
		string[] unsupported = ["$bumpmap2", "$blendmodulatetexture", "$maskedblending"];
		foreach ( var parameter in unsupported )
		{
			if ( !string.IsNullOrWhiteSpace( parameters.String( parameter ) ) && parameters.String( parameter ) != "0" )
				throw new NotSupportedException( $"{FullPath}: WorldVertexTransition {parameter} is not supported." );
		}

		var secondPath = parameters.String( "$basetexture2" );
		if ( string.IsNullOrWhiteSpace( secondPath ) )
			throw new InvalidDataException( $"{FullPath}: WorldVertexTransition requires $basetexture2." );

		var second = LoadSurfaceTexture( secondPath, parameters.Integer( "$frame2" ) );
		if ( second.IsCube )
			throw new InvalidDataException( $"{FullPath}: $basetexture2 requires a 2D texture." );

		var material = CreateSurface( "lightmappedgeneric", parameters, transforms, alpha, animation, resourceName );
		second.Bind( material, "g_tTexture2" );
		transforms.Get( "$basetexturetransform2" ).Bind( material, "Texture2" );
		material.SetFeature( "F_SOURCE_TERRAIN_BLEND", 1 );
		material.Set( "g_flSourceVertexAlpha", 0.0f );
		material.Set( "g_flSourceUseBaseAlpha", 0.0f );
		return material;
	}
}

using System;
using System.IO;

namespace Sandbox.Mounting.Source1;

internal sealed partial class SourceMaterialBuilder
{
	/// <summary>Preserves Source's ordered, texture-alpha-controlled four-layer terrain painting.</summary>
	private Material CreateMultiBlend( string resourceName )
	{
		var material = CreateShaderMaterial( "shaders/source1/multiblend.shader", resourceName );
		for ( var layer = 1; layer <= 4; layer++ )
		{
			var suffix = layer == 1 ? "" : layer.ToString();
			var path = parameters.String( $"$basetexture{suffix}" );
			if ( !string.IsNullOrWhiteSpace( path ) )
			{
				var texture = LoadSurfaceTexture( path, parameters.Integer( $"$frame{suffix}" ) );
				if ( texture.IsCube )
					throw new InvalidDataException( $"{FullPath}: multiblend layers require 2D textures." );
				texture.Bind( material, $"g_tLayer{layer}" );
			}

			var specPath = parameters.String( $"$spectexture{suffix}" );
			if ( !string.IsNullOrWhiteSpace( specPath ) )
			{
				var spec = LoadSurfaceTexture( specPath );
				if ( spec.IsCube )
					throw new InvalidDataException( $"{FullPath}: multiblend specular layers require 2D textures." );
				spec.Bind( material, $"g_tSpec{layer}" );
			}

			var scale = parameters.Float( $"$scale{suffix}", 1 );
			var rotation = parameters.Float( $"$rotation{suffix}" ) * MathF.PI / 180;
			if ( !float.IsFinite( scale ) || !float.IsFinite( rotation ) )
				throw new InvalidDataException( $"{FullPath}: multiblend transforms must be finite." );
			material.Set( $"g_vLayerTransform{layer}", new Vector3( MathF.Cos( rotation ), MathF.Sin( rotation ), scale > 0 ? scale : 1 ) );
		}
		material.SetFeature( "F_RENDER_BACKFACES", parameters.Boolean( "$nocull" ) ? 1 : 0 );
		return material;
	}
}

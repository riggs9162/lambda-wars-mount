using System;
using System.IO;

namespace Sandbox.Mounting.Source1;

internal sealed partial class SourceMaterialBuilder
{
	/// <summary>
	/// Builds Source's Water shader from a scrolling normal map, the scene refraction, depth fog and, above the
	/// surface, a Fresnel-weighted environment reflection. Planar reflection and refraction render targets are not reproduced.
	/// </summary>
	private Material CreateWater( string resourceName )
	{
		var material = CreateShaderMaterial( "shaders/source1/water.shader", resourceName );
		material.SetFeature( "F_SOURCE_ABOVE_WATER", parameters.Boolean( "$abovewater", true ) ? 1 : 0 );
		material.SetFeature( "F_RENDER_BACKFACES", parameters.Boolean( "$nocull" ) ? 1 : 0 );

		var normalPath = parameters.String( "$normalmap", "dev/water_normal" );
		var normal = LoadSurfaceTexture( normalPath, parameters.Integer( "$bumpframe" ), animation );
		if ( normal.IsCube )
			throw new InvalidDataException( $"{FullPath}: '{normalPath}' requires a 2D texture." );
		normal.Bind( material, "g_tNormal" );
		animation.GetValueOrDefault().Bind( material, normal );
		material.Set( "g_nSourceNormalEncoding", normal.Header.HiResFormat == VtfFormat.UV88 ? 1 : 0 );
		transforms.Get( transforms.Contains( "$bumptransform" ) ? "$bumptransform" : "$normalmaptransform" ).Bind( material, "Normal" );

		material.Set( "g_vSourceWaterScale", parameters.Pair( "$scale", Vector2.One ) );
		material.Set( "g_vSourceWaterScroll1", parameters.Pair( "$scroll1", Vector2.Zero ) );
		material.Set( "g_vSourceWaterScroll2", parameters.Pair( "$scroll2", Vector2.Zero ) );
		material.Set( "g_flSourceRefractAmount", parameters.Float( "$refractamount", 0.5f ) );
		material.Set( "g_flSourceReflectAmount", parameters.Float( "$reflectamount", 0.5f ) );
		material.Set( "g_vSourceRefractTint", parameters.Vector( "$refracttint", Vector3.One ) );
		material.Set( "g_vSourceReflectTint", parameters.Vector( "$reflecttint", Vector3.One ) );

		var fog = parameters.Boolean( "$fogenable" );
		material.Set( "g_flSourceFogEnable", fog );
		if ( fog )
		{
			material.Set( "g_vSourceFogColor", ToLinear( parameters.Vector( "$fogcolor", Vector3.Zero ) ) );
			material.Set( "g_vSourceFogRange", new Vector2( parameters.Float( "$fogstart" ), parameters.Float( "$fogend", 100 ) ) );
		}
		return material;
	}
}

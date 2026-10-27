using System;
using System.IO;

namespace Sandbox.Mounting.Source1;

internal sealed partial class SourceMaterialBuilder
{
	/// <summary>
	/// Builds Source's Water shader from a scrolling normal map, the scene refraction, depth fog and, above the
	/// surface, a Fresnel-weighted environment reflection. Planar reflection and refraction render targets are not reproduced.
	/// </summary>
	private Material CreateWater( string resourceName, bool sky = false )
	{
		var material = CreateShaderMaterial( sky ? "shaders/source1/sky_water.shader" : "shaders/source1/water.shader", resourceName );
		if ( !sky )
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
		if ( fog || sky )
			material.Set( "g_vSourceFogColor", ToLinear( parameters.Vector( "$fogcolor", sky ? new Vector3( 1, 0, 0 ) : Vector3.Zero ) ) );
		if ( fog )
			material.Set( "g_vSourceFogRange", new Vector2( parameters.Float( "$fogstart" ), parameters.Float( "$fogend", 100 ) ) );
		return material;
	}

	/// <summary>Builds the cubemap-based cheap-water path for a sky scene without a refraction buffer.</summary>
	internal Material CreateSkyWater( string resourceName )
	{
		if ( shaderName != "water" )
			return null;

		var material = CreateWater( resourceName, true );
		var environmentPath = parameters.String( "$envmap" );
		if ( parameters.Boolean( "$abovewater", true ) && !string.IsNullOrWhiteSpace( environmentPath )
			&& !environmentPath.Trim().Equals( "env_cubemap", StringComparison.OrdinalIgnoreCase ) )
		{
			try
			{
				var environment = LoadEnvironmentTexture( environmentPath, parameters.Integer( "$envmapframe" ) );
				if ( !environment.IsCube )
					throw new InvalidDataException( $"{FullPath}: '{environmentPath}' requires a cubemap." );
				environment.Bind( material, "g_tEnvCube", clamp: true );
				material.SetFeature( "F_SOURCE_CUBEMAP", 1 );
			}
			catch ( Exception exception ) when ( exception is IOException or InvalidDataException or NotSupportedException or ArgumentException )
			{
				Log.Warning( $"{FullPath}: sky water could not load environment '{environmentPath}': {exception.Message} Using the water fog color." );
			}
		}
		return material;
	}
}

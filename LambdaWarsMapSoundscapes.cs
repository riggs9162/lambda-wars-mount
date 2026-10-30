using System;

namespace Sandbox.Mounting.Source1;

/// <summary>An authored soundscape entity with its named sound positions already resolved to world points, keyed by position slot.</summary>
internal sealed record LambdaWarsSoundscapeEntity( string ClassName, string TargetName, string Soundscape, Vector3 Position, float Radius, bool Disabled, Dictionary<int, Vector3> Positions );

/// <summary>A trigger_soundscape volume and the name of the env_soundscape_triggerable it activates.</summary>
internal sealed record LambdaWarsSoundscapeTrigger( string Soundscape, Vector3 Mins, Vector3 Maxs, bool Disabled );

/// <summary>Reads the map data the soundscape port needs from a BSP's entities and playable boundary brushes.</summary>
internal static class LambdaWarsMapSoundscapes
{
	private const int PositionSlots = 8;

	/// <summary>Lists env_soundscape, env_soundscape_triggerable and env_soundscape_proxy entities; a proxy takes its soundscape and positions from its main entity.</summary>
	internal static LambdaWarsSoundscapeEntity[] Entities( LambdaWarsBsp bsp ) => bsp.Entities
		.Where( entity => ClassName( entity ) is "env_soundscape" or "env_soundscape_triggerable" or "env_soundscape_proxy" )
		.Select( entity =>
		{
			var source = ClassName( entity ) == "env_soundscape_proxy" ? FindByName( bsp, entity.GetValueOrDefault( "MainSoundscapeName", "" ) ) : entity;
			return new LambdaWarsSoundscapeEntity( ClassName( entity ), entity.GetValueOrDefault( "targetname", "" ),
				source?.GetValueOrDefault( "soundscape", "" ) ?? "", LambdaWarsBsp.Vector( entity.GetValueOrDefault( "origin", "0 0 0" ) ),
				LambdaWarsEntityNumbers.Read( entity.GetValueOrDefault( "radius", "0" ) ), IsDisabled( entity ), Positions( bsp, source ) );
		} ).ToArray();

	internal static LambdaWarsSoundscapeTrigger[] Triggers( LambdaWarsBsp bsp ) => bsp.Entities
		.Where( entity => ClassName( entity ) == "trigger_soundscape" )
		.Select( entity =>
		{
			var bounds = bsp.EntityBounds( entity );
			return new LambdaWarsSoundscapeTrigger( entity.GetValueOrDefault( "soundscape", "" ), bounds.Mins, bounds.Maxs, IsDisabled( entity ) );
		} ).ToArray();

	/// <summary>Lists the world bounds of every playable boundary brush. Source rests the strategic player on these playerclip brushes, so they set the ear height soundscapes measure from.</summary>
	internal static BBox[] BoundaryBrushes( LambdaWarsBsp bsp )
	{
		using var reader = bsp.Open();
		var brushes = new List<BBox>();
		foreach ( var boundary in bsp.Entities.Where( entity => ClassName( entity ) == "func_map_boundary" && !IsDisabled( entity ) ) )
		{
			var model = boundary.GetValueOrDefault( "model", "" );
			if ( !model.StartsWith( '*' ) || !int.TryParse( model[1..], out var index ) )
				continue;

			var origin = LambdaWarsBsp.Vector( boundary.GetValueOrDefault( "origin", "0 0 0" ) );
			brushes.AddRange( LambdaWarsMapLoader.ModelBrushes( bsp, reader, index ).Select( brush => BrushBounds( bsp, reader, brush ) )
				.OfType<BBox>().Select( bounds => new BBox( bounds.Mins + origin, bounds.Maxs + origin ) ) );
		}
		return brushes.ToArray();
	}

	/// <summary>Takes a brush's bounds from its axial sides; the compiler adds these as bevels to every brush.</summary>
	private static BBox? BrushBounds( LambdaWarsBsp bsp, BinaryReader reader, int brush )
	{
		reader.BaseStream.Position = bsp.Lumps[18].Offset + brush * 12;
		var firstSide = reader.ReadInt32();
		var sideCount = reader.ReadInt32();
		var mins = new float?[3];
		var maxs = new float?[3];
		for ( var side = 0; side < sideCount; side++ )
		{
			reader.BaseStream.Position = bsp.Lumps[19].Offset + (firstSide + side) * 8;
			reader.BaseStream.Position = bsp.Lumps[1].Offset + reader.ReadUInt16() * 20;
			var normal = LambdaWarsBsp.ReadVector( reader );
			var distance = reader.ReadSingle();
			for ( var axis = 0; axis < 3; axis++ )
			{
				if ( normal[axis] > 0.999f )
					maxs[axis] = distance;
				else if ( normal[axis] < -0.999f )
					mins[axis] = -distance;
			}
		}

		if ( mins.Contains( null ) || maxs.Contains( null ) )
			return null;
		return new BBox( new Vector3( mins[0].Value, mins[1].Value, mins[2].Value ), new Vector3( maxs[0].Value, maxs[1].Value, maxs[2].Value ) );
	}

	private static Dictionary<int, Vector3> Positions( LambdaWarsBsp bsp, Dictionary<string, string> entity )
	{
		var positions = new Dictionary<int, Vector3>();
		if ( entity is null )
			return positions;

		for ( var slot = 0; slot < PositionSlots; slot++ )
		{
			if ( entity.TryGetValue( $"position{slot}", out var name ) && FindByName( bsp, name ) is { } target )
				positions[slot] = LambdaWarsBsp.Vector( target.GetValueOrDefault( "origin", "0 0 0" ) );
		}
		return positions;
	}

	private static Dictionary<string, string> FindByName( LambdaWarsBsp bsp, string name ) => name.Length == 0 ? null
		: bsp.Entities.FirstOrDefault( entity => entity.GetValueOrDefault( "targetname", "" ).Equals( name, StringComparison.OrdinalIgnoreCase ) );

	private static string ClassName( Dictionary<string, string> entity ) => entity.GetValueOrDefault( "classname", "" );

	private static bool IsDisabled( Dictionary<string, string> entity ) => entity.GetValueOrDefault( "StartDisabled", "0" ) != "0";
}

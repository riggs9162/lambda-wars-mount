using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Sandbox.Mounting.Source1;

internal sealed class LambdaWarsBsp
{
	internal readonly record struct Lump( int Offset, int Length, int Version, int FourCc );
	internal readonly record struct Face( int Plane, bool Back, int FirstEdge, int EdgeCount, int TextureInfo, int Displacement );
	internal readonly record struct Edge( ushort A, ushort B );
	internal readonly record struct TextureInfo( Vector4 U, Vector4 V, int Flags, int Data );
	internal readonly record struct TextureData( int Name, int Width, int Height );
	internal readonly record struct Start( int OwnerNumber, string GroupName, Vector3 Position, Angles Rotation );

	internal byte[] Bytes { get; }
	private readonly string sourceFingerprint;
	internal Lump[] Lumps { get; } = new Lump[64];
	internal IReadOnlyList<Dictionary<string, string>> Entities { get; }
	internal IReadOnlyDictionary<Dictionary<string, string>, string[]> BuildingCountOutputs { get; }

	/// <summary>Repeated map-logic outputs of entities the game scripts itself, keyed by entity and then by output name.</summary>
	internal IReadOnlyDictionary<Dictionary<string, string>, Dictionary<string, string[]>> ScriptOutputs { get; }
	internal IReadOnlyList<Start> Starts { get; }
		internal BBox Bounds
	{
		get
		{
			using var reader = Open();
			reader.BaseStream.Position = Lumps[14].Offset;
			return new( ReadVector( reader ), ReadVector( reader ) );
		}
	}

	/// <summary>Reads the authored off-mesh connections between a recast entity and its target; the jump-down flag makes one one-way.</summary>
	internal IEnumerable<(Vector3 Start, Vector3 End, bool OneWay)> OffMeshLinks()
	{
		foreach ( var entity in Entities.Where( entity => entity.GetValueOrDefault( "classname" ) == "recast_offmesh_connection" ) )
		{
			var name = entity.GetValueOrDefault( "target", "" );
			var target = name.Length == 0 ? null : Entities.FirstOrDefault( candidate => candidate.GetValueOrDefault( "targetname", "" ).Equals( name, StringComparison.OrdinalIgnoreCase ) );
			if ( target is null || !int.TryParse( entity.GetValueOrDefault( "spawnflags", "0" ), out var flags ) || (flags & 16) != 0 )
				continue;
			yield return (Vector( entity.GetValueOrDefault( "origin", "0 0 0" ) ), Vector( target.GetValueOrDefault( "origin", "0 0 0" ) ), (flags & 32) != 0);
		}
	}

	internal string Fingerprint => sourceFingerprint ?? Convert.ToHexString( SHA256.HashData( Bytes ) ).ToLowerInvariant();

	internal LambdaWarsBsp( byte[] bytes, string fingerprint = null )
	{
		Bytes = bytes;
		sourceFingerprint = fingerprint;
		using var reader = Open();
		if ( reader.ReadUInt32() != 0x50534256 || reader.ReadInt32() is not (20 or 21) )
			throw new InvalidDataException( "Unsupported Source BSP signature or version." );

		for ( var i = 0; i < Lumps.Length; i++ )
		{
			var lump = new Lump( reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32() );
			if ( lump.Offset < 0 || lump.Length < 0 || (long)lump.Offset + lump.Length > bytes.Length )
				throw new InvalidDataException( $"BSP lump {i} is outside the file." );
			Lumps[i] = lump;
		}

		var text = Encoding.UTF8.GetString( GetLump( 0 ) );
		var parsedEntities = Regex.Matches( text, "\\{([^{}]*)\\}" ).Select( match =>
		{
			var fields = Regex.Matches( match.Groups[1].Value, "\"([^\"]*)\"\\s*\"([^\"]*)\"" );
			var values = fields.GroupBy( field => field.Groups[1].Value, StringComparer.OrdinalIgnoreCase )
				.ToDictionary( group => group.Key, group => group.Last().Groups[2].Value, StringComparer.OrdinalIgnoreCase );
			var outputs = fields.Where( field => field.Groups[1].Value.Equals( "OnBuildingCountChanged", StringComparison.OrdinalIgnoreCase ) )
				.Select( field => field.Groups[2].Value ).ToArray();
			var scripted = values.GetValueOrDefault( "classname", "" ) is "overrun_manager" or "logic_auto" or "logic_timer" or "logic_case" or "logic_relay" or "math_counter"
				? fields.Where( field => field.Groups[1].Value.StartsWith( "On", StringComparison.OrdinalIgnoreCase ) )
					.GroupBy( field => field.Groups[1].Value, StringComparer.OrdinalIgnoreCase )
					.ToDictionary( group => group.Key, group => group.Select( field => field.Groups[2].Value ).ToArray(), StringComparer.OrdinalIgnoreCase )
				: null;
			return (Values: values, Outputs: outputs, Scripted: scripted);
		} ).ToArray();
		Entities = parsedEntities.Select( entity => entity.Values ).ToArray();
		BuildingCountOutputs = parsedEntities.Where( entity => entity.Outputs.Length > 0 )
			.ToDictionary( entity => entity.Values, entity => entity.Outputs );
		ScriptOutputs = parsedEntities.Where( entity => entity.Scripted is { Count: > 0 } )
			.ToDictionary( entity => entity.Values, entity => entity.Scripted );
		Starts = Entities.Where( entity => entity.GetValueOrDefault( "classname" ) == "info_start_wars" )
			.Select( entity => new Start( int.Parse( entity.GetValueOrDefault( "ownernumber", "0" ), CultureInfo.InvariantCulture ),
				entity.GetValueOrDefault( "groupname", "" ), Vector( entity.GetValueOrDefault( "origin", "0 0 0" ) ),
				Angle( entity.GetValueOrDefault( "angles", "0 0 0" ) ) ) ).OrderBy( start => start.OwnerNumber ).ToArray();
	}

	private int[] multiBlendOffsets;

	/// <summary>Indexes packed four-layer records by counting only flagged grids in displacement order.</summary>
	internal int GetMultiBlendOffset( int displacement )
	{
		if ( multiBlendOffsets is null )
		{
			if ( Lumps[26].Length % 176 != 0 || Lumps[63].Length % 80 != 0 || Lumps[63].FourCc != 0 )
				throw new InvalidDataException( "Unsupported or truncated displacement multiblend data." );
			var offsets = new int[Lumps[26].Length / 176];
			var offset = 0;
			using var reader = Open();
			for ( var i = 0; i < offsets.Length; i++ )
			{
				reader.BaseStream.Position = Lumps[26].Offset + i * 176 + 20;
				var power = reader.ReadInt32();
				var flags = reader.ReadUInt32();
				if ( power is < 2 or > 4 )
					throw new InvalidDataException( "Invalid displacement power." );
				var count = ((1 << power) + 1) * ((1 << power) + 1);
				var hasBlend = (flags & 0xc0000000) == 0xc0000000;
				offsets[i] = hasBlend ? offset : -1;
				if ( hasBlend )
					offset = checked(offset + count);
			}
			if ( (long)offset * 80 != Lumps[63].Length )
				throw new InvalidDataException( "Displacement multiblend count does not match its BSP lump." );
			multiBlendOffsets = offsets;
		}
		if ( (uint)displacement >= multiBlendOffsets.Length )
			throw new InvalidDataException( "Invalid displacement index." );
		return multiBlendOffsets[displacement];
	}

	internal static LambdaWarsBsp ReadMetadata( Stream source )
	{
		if ( !source.CanSeek )
			throw new NotSupportedException( "Map metadata requires a seekable source." );
		var header = new byte[1036];
		source.ReadExactly( header );
		var selected = new Dictionary<int, byte[]>();
		for ( var index = 0; index < 64; index++ )
		{
			var position = 8 + index * 16;
			var offset = BitConverter.ToInt32( header, position );
			var length = BitConverter.ToInt32( header, position + 4 );
			if ( offset < 0 || length < 0 || (long)offset + length > source.Length )
				throw new InvalidDataException( $"BSP lump {index} is outside the file." );
			if ( index is 0 or 1 or 5 or 10 or 14 or 17 or 18 or 19 )
			{
				if ( length > 8 * 1024 * 1024 )
					throw new InvalidDataException( "Map metadata exceeds the current import budget." );
				var data = new byte[length];
				source.Position = offset;
				source.ReadExactly( data );
				selected[index] = data;
			}
			Array.Clear( header, position, 8 );
		}
		source.Position = 0;
		var fingerprint = Convert.ToHexString( SHA256.HashData( source ) ).ToLowerInvariant();
		using var compact = new MemoryStream();
		compact.Write( header );
		foreach ( var entry in selected )
		{
			var offset = checked((int)compact.Position);
			compact.Write( entry.Value );
			var end = compact.Position;
			compact.Position = 8 + entry.Key * 16;
			using var writer = new BinaryWriter( compact, Encoding.UTF8, true );
			writer.Write( offset );
			writer.Write( entry.Value.Length );
			compact.Position = end;
		}
		return new LambdaWarsBsp( compact.ToArray(), fingerprint );
	}

	/// <summary>Reads one validated lump directly from a BSP without allocating the complete map.</summary>
	internal static byte[] ReadLump( Stream source, int index )
	{
		if ( (uint)index >= 64 )
			throw new ArgumentOutOfRangeException( nameof( index ) );
		if ( !source.CanSeek )
			throw new NotSupportedException( "Map lump reading requires a seekable source." );

		var originalPosition = source.Position;
		try
		{
			source.Position = 0;
			using var reader = new BinaryReader( source, Encoding.UTF8, true );
			if ( reader.ReadUInt32() != 0x50534256 || reader.ReadInt32() is not (20 or 21) )
				throw new InvalidDataException( "Unsupported Source BSP signature or version." );

			source.Position = 8 + index * 16;
			var offset = reader.ReadInt32();
			var length = reader.ReadInt32();
			if ( offset < 0 || length < 0 || (long)offset + length > source.Length )
				throw new InvalidDataException( $"BSP lump {index} is outside the file." );

			var bytes = new byte[length];
			source.Position = offset;
			source.ReadExactly( bytes );
			return bytes;
		}
		finally
		{
			source.Position = originalPosition;
		}
	}

		internal BBox EntityBounds( Dictionary<string, string> entity )
	{
		var origin = Vector( entity.GetValueOrDefault( "origin", "0 0 0" ) );
		if ( !entity.TryGetValue( "model", out var model ) || !model.StartsWith( '*' ) || !int.TryParse( model[1..], out var index ) )
			return new( origin, origin );

		if ( index < 0 || (index + 1) * 48 > Lumps[14].Length )
			throw new InvalidDataException( "Entity brush model index is invalid." );

		using var reader = Open();
		reader.BaseStream.Position = Lumps[14].Offset + index * 48;
		return new( ReadVector( reader ) + origin, ReadVector( reader ) + origin );
	}

	internal BinaryReader Open() => new( new MemoryStream( Bytes, false ) );

	/// <summary>Views uncompressed fixed-size records in the retained BSP storage without allocating a second array.</summary>
	internal ReadOnlySpan<T> ReadLumpRecords<T>( int index ) where T : unmanaged
	{
		var lump = Lumps[index];
		if ( !BitConverter.IsLittleEndian || lump.FourCc != 0 || lump.Length % Unsafe.SizeOf<T>() != 0 )
			throw new InvalidDataException( $"Unsupported or truncated BSP lump {index}." );

		return MemoryMarshal.Cast<byte, T>( Bytes.AsSpan( lump.Offset, lump.Length ) );
	}

	internal byte[] GetLump( int index )
	{
		var lump = Lumps[index];
		if ( lump.FourCc != 0 )
			throw new NotSupportedException( $"Compressed BSP lump {index} is not supported." );
		return Bytes.AsSpan( lump.Offset, lump.Length ).ToArray();
	}

	internal static Vector3 ReadVector( BinaryReader reader ) => new( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );

	/// <summary>Reports whether Source draws a surface. Tool textures stay hidden, except the unlit filler that maps such as Trapped use as real ground.</summary>
	internal static bool IsDrawnSurface( string texture, int flags )
	{
		if ( (flags & 0x84) != 0 )
			return false;
		return !texture.StartsWith( "tools/", StringComparison.OrdinalIgnoreCase ) || texture.Equals( "tools/toolsnolight", StringComparison.OrdinalIgnoreCase );
	}

	internal static Vector3 Vector( string value )
	{
		var values = value.Split( (char[])null, StringSplitOptions.RemoveEmptyEntries );
		return new( values.Length > 0 ? LambdaWarsEntityNumbers.Read( values[0] ) : 0,
			values.Length > 1 ? LambdaWarsEntityNumbers.Read( values[1] ) : 0,
			values.Length > 2 ? LambdaWarsEntityNumbers.Read( values[2] ) : 0 );
	}

	internal static Angles Angle( string value )
	{
		var vector = Vector( value );
		return new( vector.x, vector.y, vector.z );
	}
}

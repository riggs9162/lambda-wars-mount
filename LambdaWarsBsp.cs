using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Sandbox.Mounting.Source1;

internal sealed class LambdaWarsBsp
{
	internal readonly record struct Lump( int Offset, int Length, int Version, int FourCc );
	internal readonly record struct Face( int Plane, bool Back, int FirstEdge, int EdgeCount, int TextureInfo, int Displacement );
	internal readonly record struct TextureInfo( Vector4 U, Vector4 V, int Flags, int Data );
	internal readonly record struct TextureData( int Name, int Width, int Height );
	internal readonly record struct Start( int OwnerNumber, string GroupName, Vector3 Position, Angles Rotation );

	internal byte[] Bytes { get; }
	private readonly string sourceFingerprint;
	internal Lump[] Lumps { get; } = new Lump[64];
	internal IReadOnlyList<Dictionary<string, string>> Entities { get; }
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
		Entities = Regex.Matches( text, "\\{([^{}]*)\\}" ).Select( match => Regex.Matches( match.Groups[1].Value, "\"([^\"]*)\"\\s*\"([^\"]*)\"" )
			.GroupBy( field => field.Groups[1].Value, StringComparer.OrdinalIgnoreCase ).ToDictionary( group => group.Key, group => group.Last().Groups[2].Value, StringComparer.OrdinalIgnoreCase ) ).ToArray();
		Starts = Entities.Where( entity => entity.GetValueOrDefault( "classname" ) == "info_start_wars" )
			.Select( entity => new Start( int.Parse( entity.GetValueOrDefault( "ownernumber", "0" ), CultureInfo.InvariantCulture ),
				entity.GetValueOrDefault( "groupname", "" ), Vector( entity.GetValueOrDefault( "origin", "0 0 0" ) ),
				Angle( entity.GetValueOrDefault( "angles", "0 0 0" ) ) ) ).OrderBy( start => start.OwnerNumber ).ToArray();
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
			if ( index is 0 or 14 )
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

	internal byte[] GetLump( int index )
	{
		var lump = Lumps[index];
		if ( lump.FourCc != 0 )
			throw new NotSupportedException( $"Compressed BSP lump {index} is not supported." );
		return Bytes.AsSpan( lump.Offset, lump.Length ).ToArray();
	}

	internal static Vector3 ReadVector( BinaryReader reader ) => new( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );

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

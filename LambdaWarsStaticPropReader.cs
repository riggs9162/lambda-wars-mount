using System;
using System.Globalization;
using System.Text;

namespace Sandbox.Mounting.Source1;

internal sealed record LambdaWarsStaticProp(
	int ModelIndex,
	string ModelPath,
	Vector3 Position,
	Angles Rotation,
	int Solid,
	int Skin,
	int FirstLeaf,
	int LeafCount );

internal sealed class LambdaWarsStaticPropMetadata
{
	internal int Version { get; init; }
	internal IReadOnlyList<string> Models { get; init; } = [];
	internal IReadOnlyList<LambdaWarsStaticProp> Props { get; init; } = [];
	internal IReadOnlyList<ushort> Leaves { get; init; } = [];
	internal int LeafCount { get; init; }
	internal int ModelCount => Models.Count;
	internal int PropCount => Props.Count;
}

/// <summary>Reads Alien Swarm static-prop version 9 metadata without constructing render or physics resources.</summary>
internal static class LambdaWarsStaticPropReader
{
	private const uint BspSignature = 0x50534256;
	private const uint StaticPropLumpId = 0x73707270;
	private const int BspHeaderSize = 1036;
	private const int StaticPropRecordSize = 72;
	private const int MaximumModels = 4096;
	private const int MaximumLeaves = 1048576;
	private const int MaximumProps = 65536;

	internal static LambdaWarsStaticPropMetadata Read( Stream source )
	{
		ArgumentNullException.ThrowIfNull( source );
		if ( !source.CanRead || !source.CanSeek )
			throw new NotSupportedException( "Static-prop metadata requires a readable, seekable BSP stream." );

		var originalPosition = source.Position;
		try
		{
			using var reader = new BinaryReader( source, Encoding.UTF8, true );
			RequireRange( source, 0, BspHeaderSize, "BSP header" );
			source.Position = 0;
			if ( reader.ReadUInt32() != BspSignature || reader.ReadInt32() is not (20 or 21) )
				throw new InvalidDataException( "Unsupported Source BSP signature or version." );

			source.Position = 8 + 35 * 16;
			var directoryOffset = reader.ReadInt32();
			var directoryLength = reader.ReadInt32();
			reader.ReadInt32();
			if ( reader.ReadInt32() != 0 )
				throw new NotSupportedException( "Compressed BSP game lumps are unsupported." );

			RequireRange( source, directoryOffset, directoryLength, "BSP game lump" );
			if ( directoryLength == 0 )
				return new();

			if ( directoryLength < 4 )
				throw new InvalidDataException( "The BSP game-lump directory is truncated." );

			source.Position = directoryOffset;
			var directoryCount = reader.ReadInt32();
			if ( directoryCount < 0 || 4L + directoryCount * 16L > directoryLength )
				throw new InvalidDataException( "The BSP game-lump directory count is invalid." );

			LambdaWarsStaticPropMetadata result = null;
			for ( var index = 0; index < directoryCount; index++ )
			{
				source.Position = directoryOffset + 4L + index * 16L;
				var id = reader.ReadUInt32();
				var flags = reader.ReadUInt16();
				var version = reader.ReadUInt16();
				var offset = reader.ReadInt32();
				var length = reader.ReadInt32();
				if ( id != StaticPropLumpId )
					continue;

				if ( result is not null )
					throw new InvalidDataException( "The BSP contains duplicate static-prop lumps." );

				if ( flags != 0 )
					throw new NotSupportedException( "Compressed or flagged static-prop lumps are unsupported." );

				if ( version != 9 )
					throw new NotSupportedException( $"Static-prop version {version} is unsupported; version 9 is required." );

				RequireRange( source, offset, length, "Static-prop lump" );
				if ( offset < directoryOffset || (long)offset + length > (long)directoryOffset + directoryLength )
					throw new InvalidDataException( "The static-prop lump is outside its BSP game lump." );

				result = ReadVersion9( reader, offset, length );
			}

			return result ?? new();
		}
		catch ( EndOfStreamException exception )
		{
			throw new InvalidDataException( "The static-prop metadata is truncated.", exception );
		}
		finally
		{
			source.Position = originalPosition;
		}
	}

	private static LambdaWarsStaticPropMetadata ReadVersion9( BinaryReader reader, long offset, int length )
	{
		var source = reader.BaseStream;
		var end = offset + length;
		source.Position = offset;
		RequireRemaining( source, end, 4 );
		var modelCount = ReadCount( reader, MaximumModels, "model" );
		RequireRemaining( source, end, modelCount * 128L + 4 );
		var models = new string[modelCount];
		for ( var index = 0; index < models.Length; index++ )
		{
			var bytes = reader.ReadBytes( 128 );
			var terminator = Array.IndexOf( bytes, (byte)0 );
			if ( terminator <= 0 )
				throw new InvalidDataException( "A static-prop model path is empty or unterminated." );

			var path = Encoding.UTF8.GetString( bytes, 0, terminator ).Replace( '\\', '/' );
			if ( !path.StartsWith( "models/", StringComparison.OrdinalIgnoreCase ) || !path.EndsWith( ".mdl", StringComparison.OrdinalIgnoreCase )
				|| path.Split( '/' ).Any( part => part is "." or ".." ) || path.Contains( ':' ) )
				throw new InvalidDataException( $"Invalid static-prop model path: {path}" );

			models[index] = path;
		}

		var leafCount = ReadCount( reader, MaximumLeaves, "leaf" );
		RequireRemaining( source, end, leafCount * 2L + 4 );
		var leaves = new ushort[leafCount];
		for ( var index = 0; index < leaves.Length; index++ )
			leaves[index] = reader.ReadUInt16();
		var propCount = ReadCount( reader, MaximumProps, "prop" );
		if ( end - source.Position != propCount * (long)StaticPropRecordSize )
			throw new InvalidDataException( "The static-prop version 9 record length is invalid." );

		var props = new LambdaWarsStaticProp[propCount];
		for ( var index = 0; index < props.Length; index++ )
		{
			var recordOffset = source.Position;
			var position = ReadFiniteVector( reader );
			var angles = ReadFiniteVector( reader );
			var modelIndex = reader.ReadUInt16();
			var firstLeaf = reader.ReadUInt16();
			var propLeaves = reader.ReadUInt16();
			var solid = reader.ReadByte();
			var propFlags = reader.ReadByte();
			var skin = reader.ReadInt32();
			ReadFiniteFloat( reader );
			ReadFiniteFloat( reader );
			var lightingOrigin = new Vector3( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
			if ( (propFlags & 2) != 0 && (!float.IsFinite( lightingOrigin.x ) || !float.IsFinite( lightingOrigin.y ) || !float.IsFinite( lightingOrigin.z )) )
				throw new InvalidDataException( "The authored static-prop lighting origin is non-finite." );
			ReadFiniteFloat( reader );
			reader.ReadBytes( 8 );
			var disabledOnXbox = reader.ReadByte();
			reader.ReadBytes( 3 );
			if ( modelIndex >= models.Length || (long)firstLeaf + propLeaves > leafCount || solid > 6 || skin < 0
				|| disabledOnXbox is not (0 or 1) || source.Position != recordOffset + StaticPropRecordSize )
				throw new InvalidDataException( $"Invalid static-prop record {index.ToString( CultureInfo.InvariantCulture )}." );

			props[index] = new( modelIndex, models[modelIndex], position, new Angles( angles.x, angles.y, angles.z ), solid, skin, firstLeaf, propLeaves );
		}

		return new() { Version = 9, Models = models, Props = props, Leaves = leaves, LeafCount = leafCount };
	}

	private static int ReadCount( BinaryReader reader, int maximum, string name )
	{
		var count = reader.ReadInt32();
		if ( count < 0 || count > maximum )
			throw new InvalidDataException( $"The static-prop {name} count exceeds its metadata budget." );

		return count;
	}

	private static Vector3 ReadFiniteVector( BinaryReader reader ) => new( ReadFiniteFloat( reader ), ReadFiniteFloat( reader ), ReadFiniteFloat( reader ) );

	private static float ReadFiniteFloat( BinaryReader reader )
	{
		var value = reader.ReadSingle();
		if ( !float.IsFinite( value ) )
			throw new InvalidDataException( "Static-prop metadata contains a non-finite value." );

		return value;
	}

	private static void RequireRange( Stream source, long offset, long length, string label )
	{
		if ( offset < 0 || length < 0 || offset > source.Length || length > source.Length - offset )
			throw new InvalidDataException( $"{label} is outside the BSP file." );
	}

	private static void RequireRemaining( Stream source, long end, long length )
	{
		if ( length < 0 || source.Position > end || length > end - source.Position )
			throw new InvalidDataException( "The static-prop metadata is truncated." );
	}
}



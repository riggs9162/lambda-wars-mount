using System;
using System.IO;

namespace Sandbox.Mounting.Source1;

/// <summary>Authored RGBExp32 bytes decoded using Valve's power2_n table definition, 2^exponent / 255.</summary>
internal readonly record struct LambdaWarsRgbExp32( byte R, byte G, byte B, sbyte Exponent )
{
	public Vector3 SourceLinear => new Vector3( R, G, B ) * (MathF.Pow( 2, Exponent ) / 255.0f);
}

/// <summary>Preserves one face's style-major, bump-plane-major RGBExp32 samples without copying the BSP lighting lump.</summary>
internal sealed record LambdaWarsFaceLighting( int FaceIndex, int TextureInfo, int MinU, int MinV, int Width, int Height,
	Vector4 U, Vector4 V, byte[] Styles, int BumpPlanes, ReadOnlyMemory<byte> Samples )
{
	public LambdaWarsRgbExp32 ReadSample( int style, int bumpPlane, int x, int y )
	{
		if ( (uint)style >= Styles.Length || (uint)bumpPlane >= BumpPlanes || (uint)x >= Width || (uint)y >= Height )
			throw new ArgumentOutOfRangeException( nameof( style ), "The requested face lightmap sample is outside its authored layout." );

		var offset = checked((((style * BumpPlanes + bumpPlane) * Height + y) * Width + x) * 4);
		var sample = Samples.Span.Slice( offset, 4 );
		return new( sample[0], sample[1], sample[2], unchecked((sbyte)sample[3]) );
	}

	public Vector2 LuxelCoordinates( Vector3 position ) => new(
		position.x * U.x + position.y * U.y + position.z * U.z + U.w - MinU,
		position.x * V.x + position.y * V.y + position.z * V.z + V.w - MinV );
}

internal sealed record LambdaWarsLightingSet( bool IsHdr, bool IsAvailable, int FaceCount, int UnlitFaceCount, long SampleCount,
	IReadOnlyList<LambdaWarsFaceLighting> Faces );

/// <summary>Reads separate LDR and HDR face lighting layouts and retains every authored style and directional bump plane.</summary>
internal static class LambdaWarsMapLighting
{
	public static LambdaWarsLightingSet Read( LambdaWarsBsp bsp, bool hdr, Action budgetCheck = null )
	{
		var faceLump = bsp.Lumps[hdr ? 58 : 7];
		var lightingLump = bsp.Lumps[hdr ? 53 : 8];
		var textureLump = bsp.Lumps[6];
		if ( faceLump.FourCc != 0 || lightingLump.FourCc != 0 || textureLump.FourCc != 0 )
			throw new NotSupportedException( "Compressed BSP lighting, faces or texture information is not supported." );
		if ( faceLump.Length % 56 != 0 || lightingLump.Length % 4 != 0 || textureLump.Length % 72 != 0 )
			throw new InvalidDataException( "A BSP lighting layout contains a truncated record." );
		if ( lightingLump.Length == 0 )
			return new( hdr, false, faceLump.Length / 56, 0, 0, [] );
		if ( faceLump.Length == 0 )
			throw new InvalidDataException( "A populated BSP lighting lump has no matching face records." );

		using var reader = bsp.Open();
		var faces = new List<LambdaWarsFaceLighting>();
		var count = faceLump.Length / 56;
		var unlit = 0;
		long samples = 0;
		for ( var i = 0; i < count; i++ )
		{
			if ( (i & 255) == 0 )
				budgetCheck?.Invoke();

			reader.BaseStream.Position = faceLump.Offset + i * 56 + 10;
			var textureInfo = reader.ReadInt16();
			reader.BaseStream.Position += 4;
			var styles = reader.ReadBytes( 4 );
			var offset = reader.ReadInt32();
			reader.ReadSingle();
			var minU = reader.ReadInt32();
			var minV = reader.ReadInt32();
			var sizeU = reader.ReadInt32();
			var sizeV = reader.ReadInt32();
			if ( offset == -1 )
			{
				unlit++;
				continue;
			}

			if ( textureInfo < 0 || textureInfo >= textureLump.Length / 72 || offset < 0 || offset % 4 != 0 || sizeU < 0 || sizeV < 0 )
				throw new InvalidDataException( $"BSP face {i} has invalid lighting indices or dimensions." );

			var styleCount = Array.IndexOf( styles, (byte)255 );
			if ( styleCount < 0 )
				styleCount = 4;
			if ( styleCount == 0 || styles.Skip( styleCount ).Any( style => style != 255 ) )
				throw new InvalidDataException( $"BSP face {i} has an invalid lightstyle terminator." );

			reader.BaseStream.Position = textureLump.Offset + textureInfo * 72 + 32;
			var u = new Vector4( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
			var v = new Vector4( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
			if ( !Finite( u ) || !Finite( v ) )
				throw new InvalidDataException( $"BSP face {i} has a nonfinite lightmap UV basis." );
			var flags = reader.ReadInt32();
			var bumpPlanes = (flags & 0x0800) != 0 ? 4 : 1;
			var sampleCount = checked(((long)sizeU + 1) * ((long)sizeV + 1) * styleCount * bumpPlanes);
			var bytes = checked(sampleCount * 4);
			if ( bytes > int.MaxValue || (long)offset + bytes > lightingLump.Length )
				throw new InvalidDataException( $"BSP face {i} lighting samples extend outside their lump." );

			faces.Add( new( i, textureInfo, minU, minV, checked(sizeU + 1), checked(sizeV + 1), u, v,
				styles[..styleCount], bumpPlanes, bsp.Bytes.AsMemory( lightingLump.Offset + offset, (int)bytes ) ) );
			samples += sampleCount;
		}
		return new( hdr, true, count, unlit, samples, faces );
	}

	private static bool Finite( Vector4 vector ) => float.IsFinite( vector.x ) && float.IsFinite( vector.y )
		&& float.IsFinite( vector.z ) && float.IsFinite( vector.w );
}

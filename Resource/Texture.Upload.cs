using System;
using System.Collections.Generic;
using System.IO;

namespace Sandbox.Mounting.Source1;

internal sealed partial class TextureLoader
{
	/// <summary>Preserves authored mip pixels through single-level uploads and GPU copies, avoiding the engine's failing bulk mip upload path.</summary>
	private static Texture CreateStagedTexture( ImageFormat format, byte[] pixels, int width, int height,
		int frames, bool cube, int mipCount )
	{
		if ( !ThreadSafe.IsMainThread || !Graphics.IsAvailable )
			throw new InvalidOperationException( "Original texture mip uploads require the main thread and an initialized renderer." );

		var imageCount = cube ? 6 : frames;
		var baseLength = checked(GetUploadLength( format, width, height ) * imageCount);
		var expectedLength = 0;
		for ( var mip = 0; mip < mipCount; mip++ )
			expectedLength = checked(expectedLength + checked(GetUploadLength( format,
				Math.Max( 1, width >> mip ), Math.Max( 1, height >> mip ) ) * imageCount));

		if ( pixels.Length != expectedLength )
			throw new InvalidDataException( "Texture mip payload does not match its decoded layout." );

		var basePixels = cube ? null : pixels.AsSpan( pixels.Length - baseLength ).ToArray();
		var texture = CreateUploadTarget( format, basePixels, width, height, frames, cube, mipCount );
		if ( texture is null || !texture.IsValid || texture.IsError )
			throw new InvalidDataException( "The engine could not allocate the original texture mip chain." );

		var staging = new List<Texture>();
		var completed = false;
		var waitAttempted = false;
		try
		{
			var offset = 0;
			var uploadLength = cube ? pixels.Length : pixels.Length - baseLength;
			for ( var mip = mipCount - 1; mip >= (cube ? 0 : 1); mip-- )
			{
				var mipWidth = Math.Max( 1, width >> mip );
				var mipHeight = Math.Max( 1, height >> mip );
				var length = GetUploadLength( format, mipWidth, mipHeight );
				for ( var image = 0; image < imageCount; image++ )
				{
					if ( length > uploadLength - offset )
						throw new InvalidDataException( "Texture mip payload exceeds its decoded extent." );

					var source = new Texture2DBuilder()
						.WithFormat( format )
						.WithSize( mipWidth, mipHeight )
						.WithMips( 1 )
						.WithData( pixels.AsSpan( offset, length ).ToArray() )
						.WithStaticUsage()
						.Finish();
					if ( source is null || !source.IsValid || source.IsError )
						throw new InvalidDataException( $"The engine could not upload original mip {mip}, image {image}." );

					staging.Add( source );
					Graphics.CopyTexture( source, texture, 0, 0, mip, image );
					offset += length;
				}
			}

			if ( offset != uploadLength )
				throw new InvalidDataException( "Texture mip payload does not match its decoded layout." );

			waitAttempted = true;
			Graphics.FlushGPU();
			completed = true;
			return texture;
		}
		finally
		{
			if ( !waitAttempted && staging.Count > 0 )
				Graphics.FlushGPU();

			foreach ( var source in staging )
				source.Dispose();

			if ( !completed )
				texture.Dispose();
		}
	}

	private static Texture CreateUploadTarget( ImageFormat format, byte[] basePixels, int width, int height,
		int frames, bool cube, int mipCount )
	{
		if ( cube )
			return new TextureCubeBuilder().WithFormat( format ).WithSize( width, height )
				.WithMips( mipCount ).WithGPUOnlyUsage().Finish();

		if ( frames > 1 )
			return new TextureArrayBuilder().WithFormat( format ).WithSize( width, height ).WithCount( frames )
				.WithMips( mipCount ).WithData( basePixels ).WithStaticUsage().Finish();

		return new Texture2DBuilder().WithFormat( format ).WithSize( width, height )
			.WithMips( mipCount ).WithData( basePixels ).WithStaticUsage().Finish();
	}

	private static int GetUploadLength( ImageFormat format, int width, int height )
	{
		var stride = format switch
		{
			ImageFormat.RGBA8888 or ImageFormat.BGRA8888 or ImageFormat.R32F => 4,
			ImageFormat.RGBA16161616 or ImageFormat.RGBA16161616F => 8,
			ImageFormat.RGBA32323232F => 16,
			_ => throw new NotSupportedException( $"Texture format {format} cannot use staged mip uploads." )
		};

		return checked(width * height * stride);
	}
}

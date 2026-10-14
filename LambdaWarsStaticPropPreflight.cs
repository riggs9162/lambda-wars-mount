using System;
using System.Text.Json;

namespace Sandbox.Mounting.Source1;

internal sealed record LambdaWarsDependencyFailure( string ModelPath, string Stage, string Message );

internal sealed record LambdaWarsTextureEstimate( string Path, int Width, int Height, int FrameCount, int FaceCount, int MipmapCount, long DecodedRgbaBytes );

internal sealed class LambdaWarsStaticPropPreflightReport
{
	public bool IsValid => IsComplete && Failures.Count == 0;
	public bool IsComplete { get; init; }
	public int PropCount { get; init; }
	public int DeclaredModelCount { get; init; }
	public int ModelCount { get; init; }
	public int InspectedModelCount { get; init; }
	public int ValidModelCount { get; init; }
	public long VertexCount { get; init; }
	public long InstanceVertexCount { get; init; }
	public int MaterialCount { get; init; }
	public int TextureCount => Textures.Count;
	public long EstimatedDecodedTextureBytes { get; init; }
	public IReadOnlyList<LambdaWarsTextureEstimate> Textures { get; init; } = [];
	public IReadOnlyList<LambdaWarsDependencyFailure> Failures { get; init; } = [];
}

/// <summary>Inspects each referenced static model once and estimates authored RGBA8 texture mip storage without allocating GPU resources.</summary>
internal static class LambdaWarsStaticPropPreflight
{
	internal static LambdaWarsStaticPropPreflightReport Inspect( BaseMount mount, LambdaWarsStaticPropMetadata metadata, Action budgetCheck = null )
	{
		ArgumentNullException.ThrowIfNull( mount );
		ArgumentNullException.ThrowIfNull( metadata );
		var instances = metadata.Props.GroupBy( prop => NormalizePath( prop.ModelPath ), StringComparer.OrdinalIgnoreCase )
			.ToDictionary( group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase );
		var failures = new List<LambdaWarsDependencyFailure>();
		var textures = new Dictionary<string, LambdaWarsTextureEstimate>( StringComparer.OrdinalIgnoreCase );
		var materials = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var inspected = 0;
		var valid = 0;
		long vertices = 0;
		long instanceVertices = 0;
		var complete = true;

		foreach ( var instance in instances )
		{
			try
			{
				budgetCheck?.Invoke();
			}
			catch ( Exception exception ) when ( exception is IOException or InvalidDataException or OperationCanceledException )
			{
				failures.Add( new( instance.Key, "Budget", exception.Message ) );
				complete = false;
				break;
			}

			var model = LambdaWarsModelInfo.Inspect( mount, instance.Key );
			inspected++;
			if ( !model.IsValid )
			{
				failures.Add( new( instance.Key, "Model", model.Error ) );
				continue;
			}

			try
			{
				vertices = checked(vertices + model.TotalVertices);
				instanceVertices = checked(instanceVertices + (long)model.TotalVertices * instance.Value);
				foreach ( var material in model.Materials )
				{
					var data = JsonSerializer.SerializeToElement( material );
					materials.Add( NormalizePath( data.GetProperty( "Path" ).GetString() ) );
				}

				foreach ( var texture in model.Textures )
				{
					var estimate = ReadTextureEstimate( JsonSerializer.SerializeToElement( texture ) );
					if ( textures.TryGetValue( estimate.Path, out var previous ) && previous != estimate )
						throw new InvalidDataException( $"Texture metadata differs between models: {estimate.Path}" );

					textures.TryAdd( estimate.Path, estimate );
				}
				valid++;
			}
			catch ( Exception exception ) when ( exception is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or OverflowException )
			{
				failures.Add( new( instance.Key, "Texture estimate", exception.Message ) );
			}
		}

		return new()
		{
			IsComplete = complete,
			PropCount = metadata.PropCount,
			DeclaredModelCount = metadata.ModelCount,
			ModelCount = instances.Count,
			InspectedModelCount = inspected,
			ValidModelCount = valid,
			VertexCount = vertices,
			InstanceVertexCount = instanceVertices,
			MaterialCount = materials.Count,
			EstimatedDecodedTextureBytes = textures.Values.Sum( texture => texture.DecodedRgbaBytes ),
			Textures = textures.Values.OrderBy( texture => texture.Path, StringComparer.OrdinalIgnoreCase ).ToArray(),
			Failures = failures.ToArray()
		};
	}

	internal static LambdaWarsTextureEstimate ReadTextureEstimate( JsonElement data )
	{
		var path = NormalizePath( data.GetProperty( "Path" ).GetString() );
		var width = data.GetProperty( "Width" ).GetInt32();
		var height = data.GetProperty( "Height" ).GetInt32();
		var frames = data.GetProperty( "FrameCount" ).GetInt32();
		var faces = data.GetProperty( "FaceCount" ).GetInt32();
		var mips = data.GetProperty( "MipmapCount" ).GetInt32();
		if ( width < 1 || width > 4096 || height < 1 || height > 4096 || frames < 1 || frames > 256 || faces is not (1 or 6 or 7)
			|| mips < 1 || mips > 1 + (int)Math.Floor( Math.Log2( Math.Max( width, height ) ) ) )
			throw new InvalidDataException( $"Texture dimensions exceed the preflight limits: {path}" );

		long pixels = 0;
		var mipWidth = width;
		var mipHeight = height;
		for ( var mip = 0; mip < mips; mip++ )
		{
			pixels = checked(pixels + (long)mipWidth * mipHeight);
			mipWidth = Math.Max( 1, mipWidth / 2 );
			mipHeight = Math.Max( 1, mipHeight / 2 );
		}

		return new( path, width, height, frames, faces, mips, checked(pixels * frames * faces * 4) );
	}

	private static string NormalizePath( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			throw new InvalidDataException( "A model dependency path is empty." );

		return path.Replace( '\\', '/' ).TrimStart( '/' ).ToLowerInvariant();
	}
}

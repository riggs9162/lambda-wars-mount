using System;
using System.IO;

namespace Sandbox.Mounting.Source1;

internal sealed record LambdaWarsLightmapPlacement( int FaceIndex, int Page, int X, int Y, int Width, int Height, int Styles, int BumpPlanes, LambdaWarsFaceLighting Face )
{
	public int PageIndex => Page;
	public int PlaneStride => Height + 2;

	public Vector4 LightmapUv( Vector3 sourceWorld )
	{
		var uv = AtlasUv( Face.LuxelCoordinates( sourceWorld ) );
		return new( uv.x, uv.y, PlaneStride / (float)LambdaWarsLightmapAtlas.PageSize, BumpPlanes == 4 ? 1 : 0 );
	}

	public Vector2 AtlasUv( Vector2 luxels, int style = 0, int bumpPlane = 0 )
	{
		if ( (uint)style >= Styles || (uint)bumpPlane >= BumpPlanes )
			throw new ArgumentOutOfRangeException( nameof( style ) );

		return new( (X + 1.5f + luxels.x) / LambdaWarsLightmapAtlas.PageSize,
			(Y + (style * BumpPlanes + bumpPlane) * PlaneStride + 1.5f + luxels.y) / LambdaWarsLightmapAtlas.PageSize );
	}
}

internal sealed record LambdaWarsLightmapAtlasData( IReadOnlyList<byte[]> Pages, IReadOnlyDictionary<int, LambdaWarsLightmapPlacement> Faces )
{
	public long RawBytes => (long)Pages.Count * LambdaWarsLightmapAtlas.PageSize * LambdaWarsLightmapAtlas.PageSize * 4;
	public int PageSize => LambdaWarsLightmapAtlas.PageSize;
}

/// <summary>Packs original RGBExp32 planes into bounded pages with independent replicated one-luxel gutters and no lighting conversion.</summary>
internal static class LambdaWarsLightmapAtlas
{
	public const int PageSize = 1024;
	public const int MaximumPages = 128;

	/// <summary>Creates the current renderer's static-style atlas, rejecting animated styles until their render uniforms exist.</summary>
	public static LambdaWarsLightmapAtlasData Create( LambdaWarsLightingSet lighting, Action budgetCheck = null, Action<long> reserveBytes = null )
	{
		foreach ( var face in lighting.Faces )
		{
			if ( face.Styles.Length != 1 || face.Styles[0] != 0 )
				throw new NotSupportedException( $"Face {face.FaceIndex} has lightstyles [{string.Join( ",", face.Styles )}]; animated or nonzero lightstyles require renderer support." );
		}
		return Build( lighting, budgetCheck, reserveBytes );
	}

	public static LambdaWarsLightmapAtlasData Build( LambdaWarsLightingSet lighting, Action budgetCheck = null, Action<long> reserveBytes = null )
	{
		if ( !lighting.IsAvailable )
			throw new InvalidDataException( "A lightmap atlas requires an available original lighting set." );

		var placements = new Dictionary<int, LambdaWarsLightmapPlacement>();
		var shelves = new List<Shelf>();
		foreach ( var face in lighting.Faces.OrderByDescending( face => checked((face.Height + 2) * face.Styles.Length * face.BumpPlanes) ).ThenByDescending( face => face.Width ) )
		{
			budgetCheck?.Invoke();
			var width = checked(face.Width + 2);
			var height = checked((face.Height + 2) * face.Styles.Length * face.BumpPlanes);
			if ( width > PageSize || height > PageSize || width < 3 || height < 3 )
				throw new NotSupportedException( $"Face {face.FaceIndex} lightmap tile {width}x{height} does not fit a {PageSize} page." );

			var shelf = shelves.FirstOrDefault( row => row.Height >= height && row.X + width <= PageSize );
			if ( shelf is null )
			{
				var page = 0;
				var y = 0;
				while ( true )
				{
					y = shelves.Where( row => row.Page == page ).Sum( row => row.Height );
					if ( y + height <= PageSize )
						break;
					page++;
					if ( page >= MaximumPages )
						throw new NotSupportedException( $"The original lightmaps exceed the {MaximumPages}-page atlas limit." );
				}
				shelf = new Shelf( page, y, height );
				shelves.Add( shelf );
			}
			placements.Add( face.FaceIndex, new( face.FaceIndex, shelf.Page, shelf.X, shelf.Y, face.Width, face.Height, face.Styles.Length, face.BumpPlanes, face ) );
			shelf.X += width;
		}

		var pageCount = shelves.Count == 0 ? 0 : shelves.Max( shelf => shelf.Page ) + 1;
		reserveBytes?.Invoke( (long)pageCount * PageSize * PageSize * 4 );
		var pages = new byte[pageCount][];
		for ( var i = 0; i < pages.Length; i++ )
		{
			budgetCheck?.Invoke();
			pages[i] = new byte[PageSize * PageSize * 4];
		}
		foreach ( var face in lighting.Faces )
		{
			budgetCheck?.Invoke();
			var placement = placements[face.FaceIndex];
			var page = pages[placement.Page];
			var source = face.Samples.Span;
			for ( var plane = 0; plane < face.Styles.Length * face.BumpPlanes; plane++ )
			{
				for ( var y = -1; y <= face.Height; y++ )
				{
					var sourceY = Math.Clamp( y, 0, face.Height - 1 );
					var sourceOffset = (plane * face.Height + sourceY) * face.Width * 4;
					var targetOffset = ((placement.Y + plane * placement.PlaneStride + y + 1) * PageSize + placement.X) * 4;
					source.Slice( sourceOffset, 4 ).CopyTo( page.AsSpan( targetOffset, 4 ) );
					source.Slice( sourceOffset, face.Width * 4 ).CopyTo( page.AsSpan( targetOffset + 4, face.Width * 4 ) );
					source.Slice( sourceOffset + (face.Width - 1) * 4, 4 ).CopyTo( page.AsSpan( targetOffset + (face.Width + 1) * 4, 4 ) );
				}
			}
		}
		return new( pages, placements );
	}

	private sealed class Shelf( int page, int y, int height )
	{
		public int Page { get; } = page;
		public int Y { get; } = y;
		public int Height { get; } = height;
		public int X { get; set; }
	}
}

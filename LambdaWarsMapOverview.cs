using System;
using System.Globalization;
using System.Text;

namespace Sandbox.Mounting.Source1;

/// <summary>Reads original overview coordinates and authored texture dimensions without creating rendering resources.</summary>
internal sealed class LambdaWarsMapOverview
{
	public bool IsValid { get; init; }
	public string TexturePath { get; init; } = "";
	public float OriginX { get; init; }
	public float OriginY { get; init; }
	public float UnitsPerPixel { get; init; }
	public int Width { get; init; }
	public int Height { get; init; }
	public bool Rotate { get; init; }
	public float Zoom { get; init; } = 1;
	public string Error { get; init; } = "";

	internal static LambdaWarsMapOverview Read( LambdaWarsMount mount, string mapId )
	{
		try
		{
			var path = $"maps/{mapId}.res";
			using var settings = mount.OpenContentFile( path );
			if ( settings.Length > 64 * 1024 )
				throw new InvalidDataException( "The original overview settings exceed 64 KB." );
			using var text = new StreamReader( settings, Encoding.UTF8 );
			var roots = VmtFile.ParseKeyValues( text.ReadToEnd(), path );
			if ( roots.Count != 1 || !roots[0].IsBlock || !roots[0].Name.Equals( mapId, StringComparison.OrdinalIgnoreCase ) )
				throw new InvalidDataException( "The original overview must contain one settings block." );
			var values = roots[0].Values;
			var material = values.GetValueOrDefault( "material", "" ).Replace( '\\', '/' );
			if ( string.IsNullOrWhiteSpace( material ) || material.StartsWith( '/' ) || material.Contains( ':' )
				|| material.Split( '/' ).Any( part => part is ".." or "." ) )
				throw new InvalidDataException( "The original overview material path is invalid." );

			var texture = $"materials/{material}.vtf";
			using var source = mount.OpenContentFile( texture );
			var header = VtfFile.ReadHeader( source );
			var originX = Number( values, "pos_x" );
			var originY = Number( values, "pos_y" );
			var scale = Number( values, "scale" );
			var rotate = Number( values, "rotate" );
			var zoom = Number( values, "zoom" );
			if ( scale <= 0 || zoom <= 0 || header.Width is < 1 or > 4096 || header.Height is < 1 or > 4096 || rotate is not (0 or 1)
				|| !float.IsFinite( scale * header.Width ) || !float.IsFinite( scale * header.Height ) )
				throw new InvalidDataException( "The original overview dimensions, scale or rotation are invalid." );
			if ( rotate != 0 )
				throw new NotSupportedException( "Rotated original overview coordinates have not been verified." );

			return new()
			{
				IsValid = true, TexturePath = $"mount://{mount.Ident}/{texture}.vtex", OriginX = originX,
				OriginY = originY, UnitsPerPixel = scale, Width = header.Width, Height = header.Height,
				Rotate = rotate != 0, Zoom = zoom
			};
		}
		catch ( Exception exception )
		{
			return new() { Error = $"Original overview is unavailable: {exception.Message}" };
		}
	}

	private static float Number( IReadOnlyDictionary<string, string> values, string key )
	{
		if ( !values.TryGetValue( key, out var text ) || !float.TryParse( text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number )
			|| !float.IsFinite( number ) )
			throw new InvalidDataException( $"The original overview field '{key}' is missing or invalid." );
		return number;
	}
}

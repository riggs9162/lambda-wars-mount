namespace Sandbox.Mounting.Source1;

/// <summary>
/// Uploads a Source color correction table as a volume texture. The file holds 32x32x32 RGB entries with red changing
/// fastest and blue slowest, so each entry's position already matches the texture's X, Y and Z axes.
/// </summary>
internal sealed class LambdaWarsColorLookupLoader( string filename ) : ResourceLoader<LambdaWarsMount>
{
	private const int Size = 32;
	private const int EntryCount = Size * Size * Size;

	protected override object Load()
	{
		var source = Host.GetFileBytes( filename );
		if ( source.Length != EntryCount * 3 )
			throw new InvalidDataException( $"'{filename}' is not a {Size}x{Size}x{Size} color lookup table." );

		var pixels = new byte[EntryCount * 4];
		for ( var entry = 0; entry < EntryCount; entry++ )
		{
			pixels[entry * 4] = source[entry * 3];
			pixels[entry * 4 + 1] = source[entry * 3 + 1];
			pixels[entry * 4 + 2] = source[entry * 3 + 2];
			pixels[entry * 4 + 3] = 255;
		}

		return Texture.CreateVolume( Size, Size, Size ).WithData( pixels ).Finish();
	}
}

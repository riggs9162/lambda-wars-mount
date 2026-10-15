using System;
using System.IO.Compression;

namespace Sandbox.Mounting.Source1;

public abstract partial class BaseMount
{
	internal IReadOnlyList<string> IndexedContentFiles() => index.Files.Keys.ToArray();

	internal void AddContentAlias( string original, string replacement )
	{
		lock ( indexLock )
		{
			if ( index.Files.ContainsKey( original ) || !index.Files.TryGetValue( replacement, out var source ) )
				return;
			index.Files.Add( original, source );
			if ( FileTypes.TryGetValue( Path.GetExtension( original ), out var resource ) )
				index.Resources.Add( (original, resource) );
		}
	}

	internal void IndexMapPackage( byte[] bytes ) => IndexMapPackage( index, bytes );

	internal static void IndexMapPackage( FileIndex target, byte[] bytes )
	{
		if ( bytes.Length == 0 )
			return;

		using var stream = new MemoryStream( bytes, false );
		using var archive = new ZipArchive( stream, ZipArchiveMode.Read );
		const long limit = 64 * 1024 * 1024;
		if ( archive.Entries.Count > 4096 )
			throw new InvalidDataException( "This map's embedded content exceeds 4096 files." );
		var files = new Dictionary<string, SourceFile>( StringComparer.OrdinalIgnoreCase );
		long total = 0;
		foreach ( var entry in archive.Entries )
		{
			var path = entry.FullName.Replace( '\\', '/' );
			if ( string.IsNullOrWhiteSpace( path ) || path.StartsWith( '/' ) || path.Contains( ':' ) || path.Contains( '\0' )
				|| path.Split( '/' ).Any( part => part is "." or ".." ) )
				throw new InvalidDataException( $"Invalid embedded resource path: {path}" );
			if ( path.EndsWith( '/' ) )
				continue;
			if ( entry.Length < 0 || entry.Length > limit - total )
				throw new InvalidDataException( "This map's embedded content exceeds the 64 MiB import budget." );
			total += entry.Length;
			var data = new byte[(int)entry.Length];
			using var input = entry.Open();
			input.ReadExactly( data );
			if ( input.ReadByte() != -1 || !files.TryAdd( path, new SourceFile( path, null, data ) ) )
				throw new InvalidDataException( $"Duplicate or oversized embedded resource: {path}" );
		}

		foreach ( var file in files )
		{
			target.Files[file.Key] = file.Value;
			target.TextureFrames.Remove( file.Key );
			target.TextureFrameErrors.Remove( file.Key );
			if ( !FileTypes.TryGetValue( Path.GetExtension( file.Key ), out var resource ) )
				continue;
			var position = target.Resources.FindIndex( value => value.Path.Equals( file.Key, StringComparison.OrdinalIgnoreCase ) );
			if ( position >= 0 )
				target.Resources[position] = (file.Key, resource);
			else
				target.Resources.Add( (file.Key, resource) );
		}
	}
}

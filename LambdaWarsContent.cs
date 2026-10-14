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

	internal void IndexMapPackage( byte[] bytes, MountContext context )
	{
		if ( bytes.Length == 0 )
			return;

		using var stream = new MemoryStream( bytes, false );
		using var archive = new ZipArchive( stream, ZipArchiveMode.Read );
		var size = archive.Entries.Sum( entry => entry.Length );
		if ( size > 64 * 1024 * 1024 || archive.Entries.Count > 4096 )
			throw new InvalidDataException( "This map's embedded content exceeds the current import memory budget." );
		foreach ( var entry in archive.Entries )
		{
			var path = entry.FullName.Replace( '\\', '/' ).TrimStart( '/' );
			if ( path.Split( '/' ).Any( part => part is "." or ".." ) || entry.Length == 0 )
				continue;

			using var input = entry.Open();
			using var output = new MemoryStream();
			input.CopyTo( output );
			if ( !index.Files.TryAdd( path, new SourceFile( path, null, output.ToArray() ) ) )
				continue;

			if ( FileTypes.TryGetValue( Path.GetExtension( path ), out var resource ) )
			{
				index.Resources.Add( (path, resource) );
				context.Add( resource.Type, path, resource.Create( path ) );
			}
		}
	}
}

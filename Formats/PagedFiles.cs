using System;
using System.Collections.Generic;
using System.IO;

namespace Sandbox.Mounting.Source1;

/// <summary>Keeps the 64 KiB pages a burst of small reads touches, so each page costs one file read and nothing reaches the large object heap.</summary>
internal sealed class PagedFile( Stream source ) : IDisposable
{
	private const int PageSize = 64 * 1024;
	private readonly Dictionary<long, byte[]> pages = [];

	internal long Length { get; } = source.Length;

	private ReadOnlySpan<byte> Page( long position )
	{
		var number = position / PageSize;
		if ( !pages.TryGetValue( number, out var page ) )
		{
			page = new byte[Math.Min( PageSize, Length - number * PageSize )];
			source.Position = number * PageSize;
			source.ReadExactly( page );
			pages.Add( number, page );
		}
		return page.AsSpan( (int)(position - number * PageSize) );
	}

	internal int Read( long position, Span<byte> buffer )
	{
		var total = 0;
		while ( !buffer.IsEmpty && position < Length )
		{
			var page = Page( position );
			var count = Math.Min( page.Length, buffer.Length );
			page[..count].CopyTo( buffer );
			buffer = buffer[count..];
			position += count;
			total += count;
		}
		return total;
	}

	public void Dispose() => source.Dispose();
}

/// <summary>Opens files through shared page caches for the length of one decoding pass.</summary>
internal sealed class PagedFiles( Func<string, Stream> openFile ) : IDisposable
{
	private readonly Dictionary<string, PagedFile> files = new( StringComparer.OrdinalIgnoreCase );

	internal Stream Open( string path )
	{
		if ( !files.TryGetValue( path, out var file ) )
			files.Add( path, file = new PagedFile( openFile( path ) ) );
		return ArchiveFile.OpenPaged( file );
	}

	public void Dispose()
	{
		foreach ( var file in files.Values )
			file.Dispose();
	}
}

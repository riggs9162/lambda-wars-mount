using System;
using System.IO;
using System.Threading.Tasks;
using Sandbox.Mounting;

namespace Sandbox.Mounting.Source1;

public abstract partial class BaseMount : BaseGameMount
{
	public abstract long AppId { get; }
	public override long? SteamAppId => AppId;
	protected abstract string ContentDirectory { get; }

	private volatile FileIndex index = new();
	private string installedDirectory;
	internal FileIndex CurrentIndex => index;
	private readonly object indexLock = new();

	internal readonly record struct ResourceFactory( ResourceType Type, Func<string, Sandbox.Mounting.ResourceLoader> Create );

	private static readonly Dictionary<string, ResourceFactory> FileTypes = new( StringComparer.OrdinalIgnoreCase )
	{
		{ ".mdl", new( ResourceType.Model, static path => new ModelLoader( path ) ) },
		{ ".vmt", new( ResourceType.Material, static path => new MaterialLoader( path ) ) },
		{ ".vtf", new( ResourceType.Texture, static path => new TextureLoader( path ) ) },
		{ ".wav", new( ResourceType.Sound, static path => new WavSoundLoader( path ) ) },
		{ ".mp3", new( ResourceType.Sound, static path => new Mp3SoundLoader( path ) ) }
	};

	protected override void Initialize( InitializeContext context )
	{
		ReplaceIndex( new FileIndex() );
		installedDirectory = null;
		IsInstalled = false;
		IsMounted = false;
		if ( !context.IsAppInstalled( AppId ) )
			return;

		var directory = context.GetAppDirectory( AppId );
		if ( !System.IO.Directory.Exists( directory ) )
			return;

		installedDirectory = directory;
		IsInstalled = true;
	}

	internal void IndexContent( string directory ) => IndexContent( ScanContent( directory ) );

	internal void IndexContent( IEnumerable<ContentSource> sources )
		=> InstallContentIndex( PrepareContentIndex( sources ) );

	internal FileIndex PrepareContentIndex( IEnumerable<ContentSource> sources )
	{
		var next = new FileIndex();
		try
		{
			IndexSources( next, sources );
			PrepareTextureFrames( next );
			var unavailable = next.RemoveUnavailableModels();
			if ( unavailable.Count > 0 )
			{
				foreach ( var diagnostic in unavailable )
					Log.Trace( $"{Title}: model was not registered: {diagnostic}" );
				Log.Trace( $"{Title}: skipped {unavailable.Count} models with unavailable geometry. {string.Join( "; ", unavailable.Take( 3 ) )}"
					+ (unavailable.Count > 3 ? "; remaining details are logged at trace verbosity." : "") );
			}
			var prepared = next;
			next = null;
			return prepared;
		}
		finally
		{
			next?.Dispose();
		}
	}

	internal void InstallContentIndex( FileIndex prepared )
	{
		ArgumentNullException.ThrowIfNull( prepared );
		ReplaceIndex( prepared );
		IsInstalled = prepared.Resources.Count > 0;
	}

	private void ReplaceIndex( FileIndex next )
	{
		FileIndex previous;
		lock ( indexLock )
		{
			previous = index;
			index = next;
		}
		previous.Dispose();
	}

	protected override void Shutdown()
	{
		installedDirectory = null;
		ReplaceIndex( new FileIndex() );
	}

	protected override Task Mount( MountContext context )
	{
		IsMounted = false;
		if ( index.Resources.Count == 0 && installedDirectory is not null )
		{
			IndexContent( installedDirectory );
		}
		var current = index;
		if ( current.Resources.Count == 0 )
		{
			context.AddError( $"No supported {Title} resources were found." );
			return Task.CompletedTask;
		}

		foreach ( var resource in current.Resources )
		{
			context.Add( resource.Factory.Type, resource.Path, resource.Factory.Create( resource.Path ) );
			if ( resource.Factory.Type == ResourceType.Texture )
				MountTextureFrames( context, current, resource.Path );
		}

		MountDecals( context, current );
		if (this is LambdaWarsMount)
			MountSoundEvents(context, current);
		try
		{
			MountSurfaces( context, current );
		}
		catch ( Exception e ) when ( e is IOException or InvalidDataException or NotSupportedException )
		{
			current.Surfaces.Clear();
			Log.Trace( $"{Title}: Source surfaces were not mounted: {e.Message}" );
			context.AddError( $"Source surfaces were not mounted: {e.Message}" );
		}

		IsMounted = true;
		return Task.CompletedTask;
	}

	internal static void PrepareTextureFrames( FileIndex files )
	{
		foreach ( var resource in files.Resources.Where( value => value.Factory.Type == ResourceType.Texture ) )
		{
			if ( files.TextureFrames.ContainsKey( resource.Path ) || files.TextureFrameErrors.ContainsKey( resource.Path ) )
				continue;
			try
			{
				using var stream = files.Files[resource.Path].Open();
				var header = VtfFile.ReadHeader( stream );
				files.TextureFrames[resource.Path] = (header.Frames, (header.Flags & VtfFlags.ENVMAP) != 0);
				files.TextureFrameErrors.Remove( resource.Path );
			}
			catch ( Exception exception ) when ( exception is IOException or InvalidDataException or NotSupportedException )
			{
				files.TextureFrameErrors[resource.Path] = exception.Message;
			}
		}
	}

	private void MountTextureFrames( MountContext context, FileIndex files, string path )
	{
		try
		{
			if ( files.TextureFrameErrors.TryGetValue( path, out var error ) )
				throw new InvalidDataException( error );
			var header = files.TextureFrames[path];
			if ( header.Frames == 0 )
				throw new InvalidDataException( "VTF frame count is zero." );
			for ( var frame = 1; frame < header.Frames; frame++ )
				context.Add( ResourceType.Texture, TextureLoader.FramePath( path, frame ), new TextureLoader( path, frame ) );
			if ( header.Frames > 1 && !header.IsEnvironment )
				context.Add( ResourceType.Texture, TextureLoader.FramePath( path, null ), new TextureLoader( path, null ) );
		}
		catch ( Exception e ) when ( e is IOException or InvalidDataException or NotSupportedException )
		{
			Log.Trace( $"{path}: texture frame loaders were not registered: {e.Message}" );
			context.AddError( $"{path}: texture frame loaders were not registered: {e.Message}" );
		}
	}

	internal string GetContentPath( string path ) =>
		index.Files.ContainsKey( path ) ? $"mount://{Ident}/{path}" : null;

}

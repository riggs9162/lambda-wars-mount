using System;

namespace Sandbox.Mounting.Source1;

public sealed class LambdaWarsMount : BaseMount
{
	protected override string ContentDirectory => "lambdawars";
	public override string Ident => "lambda_wars";
	public override string Title => "Lambda Wars";
	public override long AppId => 270370;
	private readonly List<object> maps = new();
	private string activePackage;

	protected override void Initialize( InitializeContext context )
	{
		var directory = Environment.GetEnvironmentVariable( "LAMBDA_WARS_GAME_PATH" );
		if ( string.IsNullOrWhiteSpace( directory ) && context.IsAppInstalled( AppId ) )
			directory = context.GetAppDirectory( AppId );

		if ( string.IsNullOrWhiteSpace( directory ) || !System.IO.Directory.Exists( directory ) )
			return;

		if ( File.Exists( Path.Combine( directory, "gameinfo.txt" ) ) )
			directory = Path.GetDirectoryName( Path.GetFullPath( directory ) );

		var sources = new List<ContentSource>();
		foreach ( var folder in new[] { "lambdawars", "swarm", "swarm_base", "platform" } )
		{
			var path = Path.Combine( directory, folder );
			if ( !System.IO.Directory.Exists( path ) )
				continue;

			sources.Add( new( path, false ) );
			foreach ( var package in System.IO.Directory.EnumerateFiles( path, "*.vpk", SearchOption.TopDirectoryOnly ).OrderBy( path => path ) )
			{
				var stem = Path.GetFileNameWithoutExtension( package );
				if ( stem.EndsWith( "_dir", StringComparison.OrdinalIgnoreCase ) || !stem.Contains( '_' ) )
					sources.Add( new( package, true ) );
			}
		}
		IndexContent( sources );
	}
	protected override Task Mount( MountContext context )
	{
		var selectedMap = Environment.GetEnvironmentVariable( "LAMBDA_WARS_IMPORT_MAP" ) ?? "hlw_village";
		var mapPath = $"maps/{selectedMap}.bsp";
		if ( IndexedContentFiles().Contains( mapPath, StringComparer.OrdinalIgnoreCase ) )
		{
			var selected = new LambdaWarsBsp( GetFileBytes( mapPath ) );
			IndexMapPackage( selected.GetLump( 40 ), context );
			activePackage = mapPath;
		}
		base.Mount( context ).GetAwaiter().GetResult();
		maps.Clear();
		foreach ( var name in IndexedContentFiles().Where( path => path.StartsWith( "maps/hlw_", StringComparison.OrdinalIgnoreCase ) && path.EndsWith( ".bsp", StringComparison.OrdinalIgnoreCase ) ) )
		{
			try
			{
				using var source = OpenContentFile( name );
				var bsp = LambdaWarsBsp.ReadMetadata( source );
				var id = Path.GetFileNameWithoutExtension( name );
				context.Add( ResourceType.Scene, name, new LambdaWarsMapLoader( name ) );
				maps.Add( new
				{
					Id = id,
					Title = id[4..].Replace( '_', ' ' ),
					Fingerprint = bsp.Fingerprint,
                    Center = bsp.Bounds.Center,
                    HalfSize = bsp.Bounds.Size * 0.5f,
					ScenePath = $"mount://{Ident}/{name}.scene",
					PreviewPath = $"mount://{Ident}/materials/vgui/maps/{id}.vtf.vtex",
					Starts = bsp.Starts.Select( start => new { start.OwnerNumber, start.GroupName, start.Position, start.Rotation } ).ToArray(),
										Entities = bsp.Entities.Where( entity => entity.GetValueOrDefault( "classname", "" ) is "control_point" or "trigger_capture_area" or "scrap_marker" or "scrap_marker_small" )
						.Select( entity => new
						{
							ClassName = entity.GetValueOrDefault( "classname", "" ),
							Position = LambdaWarsBsp.Vector( entity.GetValueOrDefault( "origin", "0 0 0" ) ),
							Rotation = LambdaWarsBsp.Angle( entity.GetValueOrDefault( "angles", "0 0 0" ) ),
							BoundsMins = bsp.EntityBounds( entity ).Mins,
							BoundsMaxs = bsp.EntityBounds( entity ).Maxs,
							KeyValues = entity
						} ).ToArray(),
					IsPlayable = false,
					Error = "Map geometry and navigation have not been validated."
				} );
			}
			catch ( Exception exception )
			{
				context.AddError( $"{name}: {exception.Message}" );
			}
		}

		foreach ( var name in IndexedContentFiles().Where( path => path.StartsWith( "ui/", StringComparison.OrdinalIgnoreCase ) && Path.GetExtension( path ).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" ) )
			context.Add( ResourceType.Texture, name, new LambdaWarsImageLoader( name ) );

		context.Add( ResourceType.GameResource, "catalog.json", new LambdaWarsCatalogLoader( Json.Serialize( new { Version = 1, Status = $"Original content mounted; {maps.Count} maps indexed. Map validation is required before play.", Maps = maps } ) ) );
        return Task.CompletedTask;
	}

	internal void PrepareMapPackage( string filename, byte[] package )
	{
		if ( activePackage == filename )
			return;
		if ( activePackage is not null )
			throw new InvalidOperationException( "Restart the game before importing another original map while map resource cleanup is being implemented." );
		throw new InvalidOperationException( "This original map's embedded content is not prepared. Select it with LAMBDA_WARS_IMPORT_MAP before starting the game." );
	}
}

internal sealed class LambdaWarsImageLoader( string filename ) : ResourceLoader<LambdaWarsMount>
{
	protected override object Load()
	{
		using var bitmap = Bitmap.CreateFromBytes( Host.GetFileBytes( filename ) );
		return bitmap.ToTexture();
	}
}



internal sealed class LambdaWarsCatalogLoader( string json ) : ResourceLoader<LambdaWarsMount>
{
	protected override object Load() => json;
}





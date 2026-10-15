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
	private string installationPath;
	private string steamInstallationPath;
	private string requestedMap;
	private FileIndex pendingIndex;
	private string pendingIndexPath;
	private int? stagedMapModelCount;
	private List<(string Path, object Descriptor, string Error)> pendingCatalog;
	private List<(string Path, object Descriptor, string Error)> activeCatalog = new();
	internal int ContentRevision { get; private set; }
	internal string LoadingStage { get; private set; } = "";
	internal string ActiveMapId => activePackage is null ? "" : Path.GetFileNameWithoutExtension( activePackage );
	public LambdaWarsInstallationResolution InstallationSelection { get; private set; } = new();
	private readonly Dictionary<string, LambdaWarsPreparedMap> preparedMaps = new( StringComparer.OrdinalIgnoreCase );
	internal LambdaWarsImportBudget ActiveMapImportBudget { get; set; }
	internal bool ModelsEnabled => Environment.GetEnvironmentVariable( "LAMBDA_WARS_ENABLE_3D_IMPORT" ) != "0";
	internal bool MapsEnabled => Environment.GetEnvironmentVariable( "LAMBDA_WARS_ENABLE_MAP_IMPORT" ) != "0";
	internal bool FacialMorphsEnabled => Environment.GetEnvironmentVariable( "LAMBDA_WARS_ENABLE_FACIAL_MORPHS" ) == "1";

	protected override void Initialize( InitializeContext context )
	{
		IsInstalled = true;
		steamInstallationPath = context.IsAppInstalled( AppId ) ? context.GetAppDirectory( AppId ) : null;
		InstallationSelection = LambdaWarsInstallation.Resolve( steamInstallationPath, Environment.GetEnvironmentVariable( "LAMBDA_WARS_GAME_PATH" ) );
		if ( !InstallationSelection.IsAvailable )
			return;

		var directory = InstallationSelection.InstallationPath;

		if ( pendingIndex is not null && string.Equals( pendingIndexPath, directory, StringComparison.OrdinalIgnoreCase ) )
		{
			InstallContentIndex( pendingIndex );
			pendingIndex = null;
			pendingIndexPath = null;
			activeCatalog = pendingCatalog;
			pendingCatalog = null;
		}
		else
		{
			IndexContent( InstallationSources( directory ) );
			activeCatalog = ReadMapCatalog( CurrentIndex );
		}
		LambdaWarsContentAliases.Apply( this );
		PrepareTextureFrames( CurrentIndex );
		installationPath = Path.GetFullPath( directory );
		ContentRevision++;
		IsInstalled = true;
	}

	private static IEnumerable<ContentSource> InstallationSources( string directory )
	{
		foreach ( var folder in new[] { "lambdawars", "swarm", "swarm_base", "platform" } )
		{
			var path = Path.Combine( directory, folder );
			if ( !System.IO.Directory.Exists( path ) )
				continue;

			yield return new( path, false );
			foreach ( var package in System.IO.Directory.EnumerateFiles( path, "*.vpk", SearchOption.TopDirectoryOnly ).OrderBy( path => path ) )
			{
				var stem = Path.GetFileNameWithoutExtension( package );
				if ( stem.EndsWith( "_dir", StringComparison.OrdinalIgnoreCase ) || !stem.Contains( '_' ) )
					yield return new( package, true );
			}
		}
	}

	internal Task StageInstallationAsync( string candidate ) => Task.Run( () =>
	{
		var selection = LambdaWarsInstallation.ResolveCandidate( candidate, steamInstallationPath, Environment.GetEnvironmentVariable( "LAMBDA_WARS_GAME_PATH" ) );
		if ( !selection.IsAvailable )
			throw new InvalidDataException( selection.Status );
		LoadingStage = "Indexing installation files and reading texture headers";
		var prepared = PrepareContentIndex( InstallationSources( selection.InstallationPath ) );
		try
		{
			if ( prepared.Resources.Count == 0 || !prepared.Files.Keys.Any( path => path.StartsWith( "maps/", StringComparison.OrdinalIgnoreCase ) && path.EndsWith( ".bsp", StringComparison.OrdinalIgnoreCase ) ) )
				throw new InvalidDataException( "The selected installation contains no usable resources or BSP maps." );
			var catalog = ReadMapCatalog( prepared );
			if ( !catalog.Any( entry => entry.Descriptor is not null ) )
				throw new InvalidDataException( "The selected installation contains no readable original Lambda Wars maps." );
			LambdaWarsInstallation.SaveManualPath( candidate );
			pendingIndex?.Dispose();
			pendingIndex = prepared;
			pendingIndexPath = selection.InstallationPath;
			pendingCatalog = catalog;
			requestedMap = "";
			stagedMapModelCount = null;
			prepared = null;
		}
		finally
		{
			prepared?.Dispose();
		}
	} );

	internal async Task StageMapAsync( string mapId )
	{
		await Task.Run( () => SelectMap( mapId ) );
		if ( string.Equals( ActiveMapId, mapId, StringComparison.OrdinalIgnoreCase ) )
			return;
		var root = installationPath;
		LoadingStage = $"Preparing a clean content index for {mapId}";
		var prepared = await Task.Run( () =>
		{
			var next = PrepareContentIndex( InstallationSources( root ) );
			try
			{
				using var stream = next.Files[$"maps/{mapId}.bsp"].Open();
				using var bytes = new MemoryStream();
				stream.CopyTo( bytes );
				var bsp = new LambdaWarsBsp( bytes.ToArray() );
				IndexMapPackage( next, bsp.GetLump( 40 ) );
				PrepareTextureFrames( next );
				pendingCatalog = ReadMapCatalog( next );
				stagedMapModelCount = bsp.Lumps[14].Length / 48;
				return next;
			}
			catch
			{
				next.Dispose();
				throw;
			}
		} );
		pendingIndex?.Dispose();
		pendingIndex = prepared;
		pendingIndexPath = root;
		requestedMap = mapId;
	}

	protected override void Shutdown()
	{
		activePackage = null;
		installationPath = null;
		preparedMaps.Clear();
		maps.Clear();
		activeCatalog = new();
		base.Shutdown();
	}

	internal void SelectMap( string mapId )
	{
		if ( string.IsNullOrWhiteSpace( mapId ) || mapId.Length > 128 || mapId.IndexOfAny( Path.GetInvalidFileNameChars() ) >= 0
			|| mapId.Contains( '/' ) || mapId.Contains( '\\' ) || mapId is "." or ".." )
			throw new InvalidDataException( "Select an original map ID from the installed content catalog." );
		if ( !MapsEnabled )
			throw new InvalidOperationException( "Original map loading is disabled for this process." );
		var path = $"maps/{mapId}.bsp";
		if ( !IndexedContentFiles().Contains( path, StringComparer.OrdinalIgnoreCase ) )
			throw new FileNotFoundException( $"The installed content does not contain map '{mapId}'." );
		using var source = OpenContentFile( path );
		if ( LambdaWarsBsp.ReadMetadata( source ).Starts.Count == 0 )
			throw new InvalidDataException( $"Original map '{mapId}' has no info_start_wars positions." );
	}

	private List<(string Path, object Descriptor, string Error)> ReadMapCatalog( FileIndex files )
	{
		Stream Open( string name ) => files.Files.TryGetValue( name, out var source ) ? source.Open() : throw new FileNotFoundException( name );
		var catalog = new List<(string Path, object Descriptor, string Error)>();
		var names = files.Files.Keys.Where( path => path.StartsWith( "maps/hlw_", StringComparison.OrdinalIgnoreCase ) && path.EndsWith( ".bsp", StringComparison.OrdinalIgnoreCase ) ).ToArray();
		foreach ( var name in names )
		{
			try
			{
				LoadingStage = $"Reading original map {catalog.Count + 1} of {names.Length}: {Path.GetFileNameWithoutExtension( name )}";
				using var source = Open( name );
				var bsp = LambdaWarsBsp.ReadMetadata( source );
				var staticProps = LambdaWarsStaticPropReader.Read( source );
				var id = Path.GetFileNameWithoutExtension( name );
				var overview = LambdaWarsMapOverview.Read( Open, Ident, id );
				var descriptor = new
				{
					Id = id,
					Title = id[4..].Replace( '_', ' ' ),
					Fingerprint = bsp.Fingerprint,
					StaticPropCount = staticProps.PropCount,
					StaticPropModelCount = staticProps.ModelCount,
					Center = bsp.Bounds.Center,
					HalfSize = bsp.Bounds.Size * 0.5f,
					CameraBounds = bsp.Entities.Where( entity => entity.GetValueOrDefault( "classname", "" ) == "func_map_boundary"
						&& entity.GetValueOrDefault( "StartDisabled", "0" ) != "1" ).Select( entity =>
						{
							var bounds = bsp.EntityBounds( entity );
							var bloat = LambdaWarsEntityNumbers.Read( entity.GetValueOrDefault( "bloat", "-32" ) );
							return new BBox( bounds.Mins - Vector3.One * bloat, bounds.Maxs + Vector3.One * bloat );
						} ).Where( bounds => bounds.Size.x > 0 && bounds.Size.y > 0 && bounds.Size.z > 0 ).ToArray(),
					ScenePath = $"mount://{Ident}/{name}.scene",
					PreviewPath = overview.IsValid ? overview.TexturePath : "",
					Overview = overview,
					Starts = bsp.Starts.Select( start => new { start.OwnerNumber, start.GroupName, start.Position, start.Rotation } ).ToArray(),
					Entities = bsp.Entities.Where( entity => entity.GetValueOrDefault( "classname", "" ) is "control_point" or "trigger_capture_area" or "scrap_marker" or "scrap_marker_small" or "func_nav_blocker" )
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
					CanPrepare = MapsEnabled && bsp.Starts.Count > 0,
					Error = !MapsEnabled
						? "Original map loading is disabled for this process."
						: bsp.Starts.Count == 0 ? "This original map has no info_start_wars positions." : ""
				};
				catalog.Add( (name, descriptor, "") );
			}
			catch ( Exception exception )
			{
				catalog.Add( (name, null, exception.Message) );
			}
		}
		return catalog;
	}

	protected override Task Mount( MountContext context )
	{
		context.Add( ResourceType.GameResource, "configuration.json", new LambdaWarsConfigurationLoader( steamInstallationPath, () => installationPath ) );
		context.Add( ResourceType.GameResource, "configuration-apply.json", new LambdaWarsConfigurationLoader( steamInstallationPath, () => installationPath, true ) );
		context.Add( ResourceType.GameResource, "map-selection.json", new LambdaWarsMapSelectionLoader() );
		context.Add( ResourceType.GameResource, "progress.json", new LambdaWarsProgressLoader() );
		var selectedMap = requestedMap ?? Environment.GetEnvironmentVariable( "LAMBDA_WARS_IMPORT_MAP" );
		var mapPath = $"maps/{selectedMap}.bsp";
		if ( MapsEnabled && IndexedContentFiles().Contains( mapPath, StringComparer.OrdinalIgnoreCase ) )
		{
			var modelCount = stagedMapModelCount;
			if ( modelCount is null )
			{
				var selected = new LambdaWarsBsp( GetFileBytes( mapPath ) );
				IndexMapPackage( selected.GetLump( 40 ) );
				PrepareTextureFrames( CurrentIndex );
				modelCount = selected.Lumps[14].Length / 48;
			}
			activePackage = mapPath;
			for ( var modelIndex = 0; modelIndex < modelCount.Value; modelIndex++ )
				context.Add( ResourceType.Model, $"{mapPath}.scene.model_{modelIndex}", new LambdaWarsBspModelLoader( mapPath, modelIndex ) );
		}
		base.Mount( context ).GetAwaiter().GetResult();
		IsMounted = true;
		maps.Clear();
		foreach ( var entry in activeCatalog )
		{
			if ( entry.Descriptor is null )
			{
				context.AddError( $"{entry.Path}: {entry.Error}" );
				continue;
			}
			context.Add( ResourceType.Scene, entry.Path, new LambdaWarsMapLoader( entry.Path ) );
			maps.Add( entry.Descriptor );
		}

		foreach ( var name in IndexedContentFiles().Where( path => path.StartsWith( "ui/", StringComparison.OrdinalIgnoreCase ) && Path.GetExtension( path ).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" ) )
			context.Add( ResourceType.Texture, name, new LambdaWarsImageLoader( name ) );

		var files = IndexedContentFiles();
		foreach ( var filename in files.Where( path => path.EndsWith( ".mdl", StringComparison.OrdinalIgnoreCase ) ) )
			context.Add( ResourceType.GameResource, filename + ".content.json", new LambdaWarsModelInfoLoader( filename ) );
		var companion = "models/structures/resistance/billet.dx90.vtx";
		context.Add( ResourceType.GameResource, "catalog.json", new LambdaWarsCatalogLoader( Json.Serialize( new
		{
			Version = 4,
			ContentRevision,
			ActiveMapId,
			SupportsLiveReload = true,
			Status = !InstallationSelection.IsAvailable ? InstallationSelection.Status
				: $"{InstallationSelection.Status} {maps.Count} maps indexed. " + (MapsEnabled && activePackage is not null
					? $"{selectedMap} is mounted; collision and navigation are checked during match loading."
					: MapsEnabled ? "Select an original map to prepare its content." : "Original map loading is disabled for this process."),
			InstallationPath = installationPath,
			IndexedFileCount = files.Count,
			ModelsEnabled,
			Diagnostic = (files.Contains( companion, StringComparer.OrdinalIgnoreCase )
				|| files.Contains( "models/structures/resistance/billet.vtx", StringComparer.OrdinalIgnoreCase ))
				? "Model companion-file check passed. 3D rendering still requires separate validation."
				: $"Model companion-file check failed: {companion}. Check and reapply the installation path in content settings.",
			Maps = maps
		} ) ) );
        return Task.CompletedTask;
	}

	internal LambdaWarsPreparedMap PrepareMapContent( string filename, LambdaWarsImportBudget budget )
	{
		if ( !MapsEnabled )
			throw new InvalidDataException( "Original 3D map conversion is disabled pending GPU validation." );
		if ( preparedMaps.TryGetValue( filename, out var existing ) )
			return existing;

		var bsp = new LambdaWarsBsp( GetFileBytes( filename ) );
		PrepareMapPackage( filename, bsp.GetLump( 40 ) );
		using var propSource = bsp.Open();
		var staticProps = LambdaWarsStaticPropReader.Read( propSource.BaseStream );
		budget.SetStage( "Checking static-prop dependencies" );
		var props = LambdaWarsStaticPropPreflight.Inspect( this, staticProps, budget.Check );
		if ( !props.IsValid )
			throw new InvalidDataException( $"Original map static-prop dependencies failed before rendering: {string.Join( "; ", props.Failures.Take( 3 ).Select( failure => $"{failure.ModelPath}: {failure.Message}" ) )}" );

		budget.SetStage( "Checking whole-map resource dependencies" );
		var resources = LambdaWarsMapResourcePreflight.Inspect( this, bsp, props, budget.Check );
		if ( !resources.IsValid )
			throw new InvalidDataException( $"Original map resources failed before rendering: {string.Join( "; ", resources.Failures.Take( 3 ).Select( failure => $"{failure.ModelPath}: {failure.Message}" ) )}" );

		var prepared = new LambdaWarsPreparedMap( bsp, staticProps, resources.DecodedUploadBytes );
		preparedMaps.Add( filename, prepared );
		return prepared;
	}

	internal void PrepareMapPackage( string filename, byte[] package )
	{
		if ( string.Equals( activePackage, filename, StringComparison.OrdinalIgnoreCase ) )
			return;
		throw new InvalidOperationException( $"Select '{Path.GetFileNameWithoutExtension( filename )}' through the content mount before loading its battlefield." );
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

internal sealed class LambdaWarsProgressLoader : ResourceLoader<LambdaWarsMount>
{
	protected override object Load() => Json.Serialize( new { Status = Host.LoadingStage } );
}

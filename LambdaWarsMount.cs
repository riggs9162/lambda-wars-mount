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
	public LambdaWarsInstallationResolution InstallationSelection { get; private set; } = new();
	private readonly Dictionary<string, LambdaWarsPreparedMap> preparedMaps = new( StringComparer.OrdinalIgnoreCase );
	internal LambdaWarsImportBudget ActiveMapImportBudget { get; set; }
	internal bool ModelsEnabled => Environment.GetEnvironmentVariable( "LAMBDA_WARS_ENABLE_3D_IMPORT" ) != "0";
	internal bool MapsEnabled => Environment.GetEnvironmentVariable( "LAMBDA_WARS_ENABLE_MAP_IMPORT" ) == "1";
	internal bool FacialMorphsEnabled => Environment.GetEnvironmentVariable( "LAMBDA_WARS_ENABLE_FACIAL_MORPHS" ) == "1";

	protected override void Initialize( InitializeContext context )
	{
		IsInstalled = true;
		steamInstallationPath = context.IsAppInstalled( AppId ) ? context.GetAppDirectory( AppId ) : null;
		InstallationSelection = LambdaWarsInstallation.Resolve( steamInstallationPath, Environment.GetEnvironmentVariable( "LAMBDA_WARS_GAME_PATH" ) );
		if ( !InstallationSelection.IsAvailable )
			return;

		var directory = InstallationSelection.InstallationPath;

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
		LambdaWarsContentAliases.Apply( this );
		installationPath = Path.GetFullPath( directory );
		IsInstalled = true;
	}
	protected override Task Mount( MountContext context )
	{
		context.Add( ResourceType.GameResource, "configuration.json", new LambdaWarsConfigurationLoader( steamInstallationPath, () => installationPath ) );
		context.Add( ResourceType.GameResource, "configuration-apply.json", new LambdaWarsConfigurationLoader( steamInstallationPath, () => installationPath, true ) );
		var selectedMap = Environment.GetEnvironmentVariable( "LAMBDA_WARS_IMPORT_MAP" ) ?? "hlw_village";
		var mapPath = $"maps/{selectedMap}.bsp";
		if ( MapsEnabled && IndexedContentFiles().Contains( mapPath, StringComparer.OrdinalIgnoreCase ) )
		{
			var selected = new LambdaWarsBsp( GetFileBytes( mapPath ) );
			IndexMapPackage( selected.GetLump( 40 ), context );
			activePackage = mapPath;
			for ( var modelIndex = 0; modelIndex < selected.Lumps[14].Length / 48; modelIndex++ )
				context.Add( ResourceType.Model, $"{mapPath}.scene.model_{modelIndex}", new LambdaWarsBspModelLoader( mapPath, modelIndex ) );
		}
		base.Mount( context ).GetAwaiter().GetResult();
		IsMounted = true;
		maps.Clear();
		foreach ( var name in IndexedContentFiles().Where( path => path.StartsWith( "maps/hlw_", StringComparison.OrdinalIgnoreCase ) && path.EndsWith( ".bsp", StringComparison.OrdinalIgnoreCase ) ) )
		{
			try
			{
				using var source = OpenContentFile( name );
				var bsp = LambdaWarsBsp.ReadMetadata( source );
				var staticProps = LambdaWarsStaticPropReader.Read( source );
				var id = Path.GetFileNameWithoutExtension( name );
				var overview = LambdaWarsMapOverview.Read( this, id );
				context.Add( ResourceType.Scene, name, new LambdaWarsMapLoader( name ) );
				maps.Add( new
				{
					Id = id,
					Title = id[4..].Replace( '_', ' ' ),
					Fingerprint = bsp.Fingerprint,
					StaticPropCount = staticProps.PropCount,
					StaticPropModelCount = staticProps.ModelCount,
                    Center = bsp.Bounds.Center,
                    HalfSize = bsp.Bounds.Size * 0.5f,
					ScenePath = $"mount://{Ident}/{name}.scene",
					PreviewPath = overview.IsValid ? overview.TexturePath : "",
					Overview = overview,
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
					CanPrepare = MapsEnabled && string.Equals( activePackage, name, StringComparison.OrdinalIgnoreCase ),
					Error = !MapsEnabled
						? "Original map preparation is disabled. Enable original map loading and restart the game."
						: !string.Equals( activePackage, name, StringComparison.OrdinalIgnoreCase )
							? $"This session mounted {selectedMap}. Select {id} with LAMBDA_WARS_IMPORT_MAP and restart the game before preparing it."
							: ""
				} );
			}
			catch ( Exception exception )
			{
				context.AddError( $"{name}: {exception.Message}" );
			}
		}

		foreach ( var name in IndexedContentFiles().Where( path => path.StartsWith( "ui/", StringComparison.OrdinalIgnoreCase ) && Path.GetExtension( path ).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" ) )
			context.Add( ResourceType.Texture, name, new LambdaWarsImageLoader( name ) );

		var files = IndexedContentFiles();
		foreach ( var filename in files.Where( path => path.EndsWith( ".mdl", StringComparison.OrdinalIgnoreCase ) ) )
			context.Add( ResourceType.GameResource, filename + ".content.json", new LambdaWarsModelInfoLoader( filename ) );
		var companion = "models/structures/resistance/billet.dx90.vtx";
		context.Add( ResourceType.GameResource, "catalog.json", new LambdaWarsCatalogLoader( Json.Serialize( new
		{
			Version = 3,
			Status = !InstallationSelection.IsAvailable ? InstallationSelection.Status
				: $"{InstallationSelection.Status} {maps.Count} maps indexed. " + (MapsEnabled && activePackage is not null
				? $"{selectedMap} can be prepared; collision and navigation are checked during match loading."
				: "Original map preparation is disabled for this session."),
			InstallationPath = installationPath,
			IndexedFileCount = files.Count,
			ModelsEnabled,
			Diagnostic = (files.Contains( companion, StringComparer.OrdinalIgnoreCase )
				|| files.Contains( "models/structures/resistance/billet.vtx", StringComparer.OrdinalIgnoreCase ))
				? "Model companion-file check passed. 3D rendering still requires separate validation."
				: $"Model companion-file check failed: {companion}. Check the installation path and restart the editor.",
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

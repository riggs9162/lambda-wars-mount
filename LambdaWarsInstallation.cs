using System;
using System.Text.Json;

namespace Sandbox.Mounting.Source1;

public sealed class LambdaWarsInstallationResolution
{
	public string InstallationPath { get; init; } = "";
	public string ConfiguredInstallationPath { get; init; } = "";
	public string Source { get; init; } = "None";
	public string Error { get; init; } = "";
	public string Status { get; init; } = "";
	public string ConfigurationPath { get; init; } = "";
	public bool IsAvailable => !string.IsNullOrEmpty( InstallationPath );
}

/// <summary>Validates and persists a user-selected read-only installation independently of the installed game's files.</summary>
public static class LambdaWarsInstallation
{
	private sealed class Configuration
	{
		public string InstallationPath { get; set; } = "";
	}

	public static string ConfigurationPath => Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "LambdaWarsSbox", "content.json" );

	public static LambdaWarsInstallationResolution Resolve( string steamPath, string environmentPath ) => Resolve( steamPath, environmentPath, ConfigurationPath );

	internal static LambdaWarsInstallationResolution Resolve( string steamPath, string environmentPath, string configurationPath )
		=> Resolve( steamPath, environmentPath, configurationPath, LambdaWarsSteamDiscovery.FindInstallation );

	internal static LambdaWarsInstallationResolution Resolve( string steamPath, string environmentPath, string configurationPath, Func<LambdaWarsSteamDiscoveryResult> discoverSteam )
		=> ResolveCore( steamPath, environmentPath, configurationPath, discoverSteam, true );

	internal static LambdaWarsInstallationResolution ResolveCandidate( string candidate, string steamPath, string environmentPath )
	{
		if ( string.IsNullOrWhiteSpace( candidate ) )
			return ResolveCore( steamPath, environmentPath, ConfigurationPath, LambdaWarsSteamDiscovery.FindInstallation, false );
		if ( !TryValidate( candidate, out var normalized, out var error ) )
			throw new InvalidDataException( error );
		return Selection( normalized, normalized, "Manual", new List<string>(), ConfigurationPath );
	}

	private static LambdaWarsInstallationResolution ResolveCore( string steamPath, string environmentPath, string configurationPath,
		Func<LambdaWarsSteamDiscoveryResult> discoverSteam, bool readConfiguration )
	{
		var configured = "";
		var errors = new List<string>();
		try
		{
			if ( readConfiguration && System.IO.Directory.Exists( configurationPath ) )
				throw new InvalidDataException( "The content settings path points to a folder instead of a JSON file." );

			if ( readConfiguration && File.Exists( configurationPath ) )
			{
				if ( new FileInfo( configurationPath ).Length > 64 * 1024 )
					throw new InvalidDataException( "The content settings file exceeds 64 KB." );

				var config = JsonSerializer.Deserialize<Configuration>( File.ReadAllText( configurationPath ), new JsonSerializerOptions { PropertyNameCaseInsensitive = true } )
					?? throw new InvalidDataException( "The content settings file must contain a JSON settings object." );
				configured = config.InstallationPath?.Trim() ?? "";
			}
		}
		catch ( Exception exception ) when ( exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException )
		{
			errors.Add( $"Could not read manual content settings: {exception.Message}" );
		}

		if ( !string.IsNullOrWhiteSpace( configured ) )
		{
			if ( TryValidate( configured, out var manual, out var manualError ) )
				return Selection( manual, configured, "Manual", errors, configurationPath );

			errors.Add( $"Manual installation path was rejected: {manualError}" );
		}

		if ( !string.IsNullOrWhiteSpace( steamPath ) )
		{
			if ( TryValidate( steamPath, out var steam, out var steamError ) )
				return Selection( steam, configured, "Steam", errors, configurationPath );

			errors.Add( $"The Steam installation could not be used: {steamError}" );
		}

		var discoveredSteam = discoverSteam();
		if ( !string.IsNullOrEmpty( discoveredSteam.InstallationPath ) )
			return Selection( discoveredSteam.InstallationPath, configured, "Steam", errors, configurationPath );

		if ( !string.IsNullOrWhiteSpace( discoveredSteam.Error ) )
			errors.Add( discoveredSteam.Error );

		if ( !string.IsNullOrWhiteSpace( environmentPath ) )
		{
			if ( TryValidate( environmentPath, out var environment, out var environmentError ) )
				return Selection( environment, configured, "Environment", errors, configurationPath );

			errors.Add( $"The environment installation path was rejected: {environmentError}" );
		}

		return Selection( "", configured, "None", errors, configurationPath );
	}

	public static bool TryValidate( string candidate, out string installationPath, out string error )
	{
		installationPath = "";
		error = "";
		try
		{
			var path = candidate?.Trim() ?? "";
			if ( path.Length > 4096 )
				throw new InvalidDataException( "The installation path exceeds 4096 characters." );
			if ( path.Length >= 2 && path[0] == '"' && path[^1] == '"' )
				path = path[1..^1];

			if ( string.IsNullOrWhiteSpace( path ) || !Path.IsPathFullyQualified( path ) )
				throw new InvalidDataException( "Enter the full path to the Lambda Wars installation folder or its lambdawars subfolder." );

			path = Path.TrimEndingDirectorySeparator( Path.GetFullPath( path ) );
			if ( !System.IO.Directory.Exists( path ) )
				throw new DirectoryNotFoundException( $"The selected folder does not exist: {path}" );

			var content = Path.Combine( path, "lambdawars" );
			if ( File.Exists( Path.Combine( path, "gameinfo.txt" ) ) && Path.GetFileName( path ).Equals( "lambdawars", StringComparison.OrdinalIgnoreCase ) )
			{
				content = path;
				path = Path.GetDirectoryName( path );
			}

			if ( !File.Exists( Path.Combine( content, "gameinfo.txt" ) ) )
				throw new FileNotFoundException( "The selected installation must contain lambdawars/gameinfo.txt." );

			if ( !System.IO.Directory.Exists( Path.Combine( content, "maps" ) ) && !ContainsPackedMaps( content ) )
				throw new InvalidDataException( "The selected installation contains no maps folder or packed BSP maps." );

			installationPath = path;
			return true;
		}
		catch ( Exception exception ) when ( exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException )
		{
			error = exception.Message;
			return false;
		}
	}

	public static string SaveManualPath( string path ) => SaveManualPath( path, ConfigurationPath );

	internal static string SaveManualPath( string path, string configurationPath )
	{
		var normalized = "";
		if ( !string.IsNullOrWhiteSpace( path ) && !TryValidate( path, out normalized, out var error ) )
			throw new InvalidDataException( error );

		var directory = Path.GetDirectoryName( Path.GetFullPath( configurationPath ) );
		System.IO.Directory.CreateDirectory( directory );
		var temporary = Path.Combine( directory, $"content-{Guid.NewGuid():N}.tmp" );
		try
		{
			File.WriteAllText( temporary, JsonSerializer.Serialize( new Configuration { InstallationPath = normalized }, new JsonSerializerOptions { WriteIndented = true } ) );
			File.Move( temporary, configurationPath, true );
		}
		finally
		{
			if ( File.Exists( temporary ) )
				File.Delete( temporary );
		}
		return normalized;
	}

	private static bool ContainsPackedMaps( string content )
	{
		foreach ( var path in System.IO.Directory.EnumerateFiles( content, "*.vpk", SearchOption.TopDirectoryOnly ) )
		{
			var stem = Path.GetFileNameWithoutExtension( path );
			if ( !stem.EndsWith( "_dir", StringComparison.OrdinalIgnoreCase ) && stem.Contains( '_' ) )
				continue;

			using var package = new VpkArchive( path );
			if ( package.GetFilePaths().Any( file => file.StartsWith( "maps/", StringComparison.OrdinalIgnoreCase ) && file.EndsWith( ".bsp", StringComparison.OrdinalIgnoreCase ) ) )
				return true;
		}
		return false;
	}

	private static LambdaWarsInstallationResolution Selection( string path, string configured, string source, IReadOnlyList<string> errors, string configurationPath )
	{
		var status = string.IsNullOrEmpty( path ) ? "No usable Lambda Wars installation was found. Choose the original game's installation folder."
			: $"Using the {source.ToLowerInvariant()} installation: {path}";
		var error = string.Join( " ", errors );
		return new()
		{
			InstallationPath = path,
			ConfiguredInstallationPath = configured,
			Source = source,
			Error = error,
			Status = string.IsNullOrEmpty( error ) ? status : $"{error} {status}",
			ConfigurationPath = configurationPath
		};
	}
}


using System;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace Sandbox.Mounting.Source1;

internal sealed record LambdaWarsSteamDiscoveryResult( string InstallationPath = "", string Error = "" );

/// <summary>Finds Lambda Wars from read-only Steam registry entries, library folders and app manifests.</summary>
internal static class LambdaWarsSteamDiscovery
{
	internal static LambdaWarsSteamDiscoveryResult FindInstallation()
	{
		if ( !OperatingSystem.IsWindows() )
			return new( Error: "Automatic Steam registry discovery is only available on Windows." );

		var errors = new List<string>();
		var roots = new List<string>();
		ReadRegistryPath( RegistryHive.CurrentUser, RegistryView.Default, "SteamPath", roots, errors );
		ReadRegistryPath( RegistryHive.LocalMachine, RegistryView.Registry64, "InstallPath", roots, errors );
		ReadRegistryPath( RegistryHive.LocalMachine, RegistryView.Registry32, "InstallPath", roots, errors );
		if ( roots.Count == 0 )
			return new( Error: errors.Count == 0 ? "Steam was not found in the Windows registry." : string.Join( " ", errors.Distinct() ) );

		return FindInRoots( roots, errors );
	}

	internal static LambdaWarsSteamDiscoveryResult FindInRoots( IEnumerable<string> steamRoots, IEnumerable<string> initialErrors = null )
	{
		var errors = initialErrors?.ToList() ?? new List<string>();
		var libraries = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var manifestFound = false;
		foreach ( var candidate in steamRoots )
		{
			try
			{
				if ( string.IsNullOrWhiteSpace( candidate ) || !Path.IsPathFullyQualified( candidate ) )
					throw new InvalidDataException( "A registered Steam installation path is not absolute." );

				var root = Path.TrimEndingDirectorySeparator( Path.GetFullPath( candidate ) );
				var paths = new List<string> { root };
				var metadata = Path.Combine( root, "steamapps", "libraryfolders.vdf" );
				if ( File.Exists( metadata ) )
				{
					try
					{
						paths.AddRange( ReadLibraries( ReadMetadata( metadata ), "Steam libraryfolders.vdf" ) );
					}
					catch ( Exception exception ) when ( IsDiscoveryError( exception ) )
					{
						errors.Add( $"Steam library metadata could not be read: {exception.Message}" );
					}
				}

				foreach ( var library in paths )
				{
					if ( !Path.IsPathFullyQualified( library ) )
					{
						errors.Add( "A Steam library path is not absolute." );
						continue;
					}

					var normalized = Path.TrimEndingDirectorySeparator( Path.GetFullPath( library ) );
					if ( !libraries.Add( normalized ) )
						continue;

					var manifest = Path.Combine( normalized, "steamapps", "appmanifest_270370.acf" );
					if ( !File.Exists( manifest ) )
						continue;

					manifestFound = true;
					try
					{
						var installDirectory = ReadInstallDirectory( ReadMetadata( manifest ) );
						var installation = Path.Combine( normalized, "steamapps", "common", installDirectory );
						if ( LambdaWarsInstallation.TryValidate( installation, out var path, out var error ) )
							return new( path );

						errors.Add( $"Steam's Lambda Wars installation could not be used: {error}" );
					}
					catch ( Exception exception ) when ( IsDiscoveryError( exception ) )
					{
						errors.Add( $"Steam's Lambda Wars app manifest could not be read: {exception.Message}" );
					}
				}
			}
			catch ( Exception exception ) when ( IsDiscoveryError( exception ) )
			{
				errors.Add( $"A registered Steam installation could not be checked: {exception.Message}" );
			}
		}

		if ( !manifestFound )
			errors.Add( "Steam's Lambda Wars app manifest was not found in its registered libraries." );

		return new( Error: string.Join( " ", errors.Distinct().Take( 4 ) ) );
	}

	internal static IReadOnlyList<string> ReadLibraries( string text, string sourceName = "Steam libraryfolders.vdf" )
	{
		var roots = VmtFile.ParseKeyValues( PrepareSteamEscapes( text ), sourceName );
		var folders = roots.FirstOrDefault( root => root.Name.Equals( "libraryfolders", StringComparison.OrdinalIgnoreCase ) )
			?? throw new InvalidDataException( "Steam library metadata contains no libraryfolders block." );
		var paths = new List<string>();
		foreach ( var pair in folders.Values )
		{
			if ( uint.TryParse( pair.Key, out _ ) )
				paths.Add( pair.Value );
		}
		foreach ( var folder in folders.Children )
		{
			if ( uint.TryParse( folder.Name, out _ ) && folder.Values.TryGetValue( "path", out var path ) )
				paths.Add( path );
		}
		return paths.Distinct( StringComparer.OrdinalIgnoreCase ).ToArray();
	}

	internal static string ReadInstallDirectory( string text )
	{
		var roots = VmtFile.ParseKeyValues( PrepareSteamEscapes( text ), "Steam appmanifest_270370.acf" );
		var state = roots.FirstOrDefault( root => root.Name.Equals( "AppState", StringComparison.OrdinalIgnoreCase ) )
			?? throw new InvalidDataException( "The Lambda Wars app manifest contains no AppState block." );
		if ( state.Values.TryGetValue( "appid", out var appId ) && appId != "270370" )
			throw new InvalidDataException( "The app manifest identifies a different Steam game." );

		if ( !state.Values.TryGetValue( "installdir", out var directory ) || string.IsNullOrWhiteSpace( directory )
			|| directory is "." or ".." || directory.IndexOfAny( Path.GetInvalidFileNameChars() ) >= 0
			|| directory.Contains( '/' ) || directory.Contains( '\\' ) || Path.IsPathRooted( directory ) )
			throw new InvalidDataException( "The Lambda Wars app manifest has an invalid installdir folder name." );

		return directory;
	}

	private static string PrepareSteamEscapes( string text )
	{
		ArgumentNullException.ThrowIfNull( text );
		var result = new StringBuilder( text.Length );
		var quoted = false;
		for ( var position = 0; position < text.Length; position++ )
		{
			var character = text[position];
			if ( !quoted && character == '/' && position + 1 < text.Length && text[position + 1] == '/' )
			{
				while ( position < text.Length && text[position] != '\n' )
					result.Append( text[position++] );

				if ( position < text.Length )
					result.Append( text[position] );
				continue;
			}

			if ( quoted && character == '\\' && position + 1 < text.Length )
			{
				var next = text[position + 1];
				if ( next == '\\' )
				{
					result.Append( '/' );
					position++;
					continue;
				}
				if ( next == '"' )
				{
					result.Append( character ).Append( next );
					position++;
					continue;
				}
			}
			if ( character == '"' )
				quoted = !quoted;

			result.Append( character );
		}
		return result.ToString();
	}

	private static string ReadMetadata( string path )
	{
		if ( new FileInfo( path ).Length > 2 * 1024 * 1024 )
			throw new InvalidDataException( "Steam metadata exceeds the 2 MB discovery limit." );

		return File.ReadAllText( path );
	}

	[SupportedOSPlatform( "windows" )]
	private static void ReadRegistryPath( RegistryHive hive, RegistryView view, string valueName, ICollection<string> roots, ICollection<string> errors )
	{
		try
		{
			using var registry = RegistryKey.OpenBaseKey( hive, view );
			using var steam = registry.OpenSubKey( @"SOFTWARE\Valve\Steam", writable: false );
			if ( steam?.GetValue( valueName ) is string path && !string.IsNullOrWhiteSpace( path ) )
				roots.Add( path );
		}
		catch ( Exception exception ) when ( IsDiscoveryError( exception ) )
		{
			errors.Add( $"Steam registry access failed for {hive}/{view}: {exception.Message}" );
		}
	}

	private static bool IsDiscoveryError( Exception exception ) => exception is IOException or InvalidDataException or UnauthorizedAccessException
		or SecurityException or ArgumentException or NotSupportedException;
}

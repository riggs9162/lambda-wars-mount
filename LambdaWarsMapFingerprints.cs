using System;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sandbox.Mounting.Source1;

/// <summary>Caches the SHA-256 of each original map file, keyed by its full path, length and write time, so unchanged maps are not hashed on every catalog read.</summary>
internal static class LambdaWarsMapFingerprints
{
	/// <summary>The largest cache file that is read; a larger file is treated as empty.</summary>
	private const long MaxCacheBytes = 1024 * 1024;

	/// <summary>Guards the in-memory cache and the dirty flag, and serializes writes to the cache file.</summary>
	private static readonly object cacheLock = new();

	/// <summary>Cached hashes keyed by path, length and write time. Null until the cache is first needed.</summary>
	private static Dictionary<string, string> entries;

	/// <summary>True when a hash was added since the cache was last written.</summary>
	private static bool dirty;

	/// <summary>The cache file in the user's local application data folder.</summary>
	private static string CachePath => Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "LambdaWarsSbox", "map-fingerprints.json" );

	/// <summary>Returns the lowercase hex SHA-256 of the map file.</summary>
	internal static string Get( string physicalPath, Func<Stream> open )
	{
		if ( string.IsNullOrEmpty( physicalPath ) || !Path.IsPathFullyQualified( physicalPath ) || !File.Exists( physicalPath ) )
			return Hash( open );

		var fullPath = Path.GetFullPath( physicalPath );
		var key = $"{fullPath}|{new FileInfo( fullPath ).Length}|{File.GetLastWriteTimeUtc( fullPath ).Ticks}";

		lock ( cacheLock )
		{
			entries ??= Load();
			if ( entries.TryGetValue( key, out var cached ) )
				return cached;
		}

		var hash = Hash( open );

		lock ( cacheLock )
		{
			DropStale( fullPath, key );
			entries[key] = hash;
			dirty = true;
		}

		return hash;
	}

	/// <summary>Writes the cache file if anything changed since it was read.</summary>
	internal static void Save()
	{
		lock ( cacheLock )
		{
			if ( !dirty )
				return;

			try
			{
				var path = CachePath;
				System.IO.Directory.CreateDirectory( Path.GetDirectoryName( path ) );
				var temporary = path + ".tmp";
				File.WriteAllText( temporary, JsonSerializer.Serialize( entries, new JsonSerializerOptions { WriteIndented = true } ) );
				File.Move( temporary, path, overwrite: true );
				dirty = false;
			}
			catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException )
			{
				Log.Warning( $"Could not save the map fingerprint cache: {exception.Message}" );
			}
		}
	}

	/// <summary>Hashes the stream that open returns and disposes it.</summary>
	private static string Hash( Func<Stream> open )
	{
		using var stream = open();
		return Convert.ToHexStringLower( SHA256.HashData( stream ) );
	}

	/// <summary>Reads the cache file. A missing, oversized, unreadable or malformed file yields an empty cache.</summary>
	private static Dictionary<string, string> Load()
	{
		var loaded = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		try
		{
			if ( !File.Exists( CachePath ) || new FileInfo( CachePath ).Length > MaxCacheBytes )
				return loaded;

			var stored = JsonSerializer.Deserialize<Dictionary<string, string>>( File.ReadAllText( CachePath ) );
			if ( stored == null || stored.Values.Any( value => !IsFingerprint( value ) ) )
				return loaded;

			foreach ( var (key, value) in stored )
				loaded[key] = value;
		}
		catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException )
		{
			loaded.Clear();
		}
		return loaded;
	}

	/// <summary>True when the value is exactly 64 lowercase hexadecimal characters.</summary>
	private static bool IsFingerprint( string value )
	{
		if ( value is not { Length: 64 } )
			return false;

		foreach ( var character in value )
		{
			if ( character is not ( >= '0' and <= '9' or >= 'a' and <= 'f' ) )
				return false;
		}

		return true;
	}

	/// <summary>Removes cached hashes for the same file path whose length or write time differs from the new key.</summary>
	private static void DropStale( string fullPath, string key )
	{
		var prefix = fullPath + "|";
		var stale = entries.Keys
			.Where( existing => existing.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) && !string.Equals( existing, key, StringComparison.OrdinalIgnoreCase ) )
			.ToList();

		foreach ( var obsolete in stale )
			entries.Remove( obsolete );
	}
}

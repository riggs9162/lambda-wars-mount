using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Sandbox.Mounting.Source1;

/// <summary>A Source script interval: the lowest value plus the random span above it.</summary>
internal readonly record struct SourceInterval( float Start, float Range )
{
	/// <summary>Reads "min,max" as Source does: each side is a numeric prefix, and a lone value has no span.</summary>
	internal static SourceInterval Read( string text )
	{
		var parts = text.Split( ',', StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length == 0 )
			return default;

		var start = LambdaWarsEntityNumbers.Read( parts[0] );
		return new( start, parts.Length > 1 ? LambdaWarsEntityNumbers.Read( parts[1] ) - start : 0 );
	}
}

/// <summary>One ordered command of a soundscape. Kinds keep their script names; playback order is authored order.</summary>
internal sealed class SourceSoundscapeCommand
{
	public string Kind { get; init; }
	public float Value { get; init; }
	public string Name { get; init; } = "";
	public SourceInterval Volume { get; init; }
	public SourceInterval Pitch { get; init; }
	public SourceInterval SoundLevel { get; init; }
	public SourceInterval Time { get; init; }
	public IReadOnlyList<string> Waves { get; init; } = [];
	public int Position { get; init; } = -1;
	public bool RandomPosition { get; init; }
	public Vector3? Origin { get; init; }
	public float Radius { get; init; }
	public int PositionOverride { get; init; } = -1;
	public int AmbientPositionOverride { get; init; } = -1;
}

/// <summary>
/// Reads soundscape scripts the way Source's soundscape systems do. Script errors never reach playback: a soundscape
/// section with no keys is not registered, unknown commands are skipped, and an unknown sound level means SNDLVL_NORM.
/// "dsp_player" and "Soundmixer" are skipped because no installed soundscape uses them.
/// </summary>
internal static class SourceSoundscapeScripts
{
	private const string Manifest = "scripts/soundscapes_manifest.txt";
	private const string WavePrefixes = "*?!#><^@)}(";

	/// <summary>Loads the manifest's files in order and then the map's own file, with later names replacing earlier ones. Keys are lowercase.</summary>
	internal static Dictionary<string, IReadOnlyList<SourceSoundscapeCommand>> Read( Func<string, string> read, Func<string, bool> exists, string mapId, Action<string> report )
	{
		var soundscapes = new Dictionary<string, IReadOnlyList<SourceSoundscapeCommand>>();
		if ( !exists( Manifest ) )
			return soundscapes;

		var roots = ScriptManifest.Parse( read( Manifest ), Manifest, report );
		if ( roots.Count == 0 )
			return soundscapes;

		var mapFile = string.IsNullOrEmpty( mapId ) ? null : $"scripts/soundscapes_{mapId}.txt".ToLowerInvariant();
		foreach ( var entry in roots[0].Children )
		{
			if ( entry.IsBlock || !entry.Name.Equals( "file", StringComparison.OrdinalIgnoreCase ) )
			{
				report( $"{Manifest}: unsupported manifest entry '{entry.Name}'." );
				continue;
			}

			var file = ScriptManifest.ContentPath( entry.Value );
			if ( file == mapFile )
				mapFile = null;
			AddFile( soundscapes, file, read, exists, report );
		}

		if ( mapFile is not null )
			AddFile( soundscapes, mapFile, read, exists, report );
		return soundscapes;
	}

	private static void AddFile( Dictionary<string, IReadOnlyList<SourceSoundscapeCommand>> soundscapes, string file, Func<string, string> read, Func<string, bool> exists, Action<string> report )
	{
		if ( !exists( file ) )
			return;

		try
		{
			foreach ( var root in ScriptManifest.Parse( read( file ), file, report ) )
			{
				if ( root.IsBlock && root.Children.Count > 0 )
					soundscapes[root.Name.ToLowerInvariant()] = root.Children.Select( Command ).Where( command => command is not null ).ToArray();
			}
		}
		catch ( Exception exception ) when ( exception is IOException or InvalidDataException or NotSupportedException )
		{
			report( $"{file}: soundscape script was not read: {exception.Message}" );
		}
	}

	private static SourceSoundscapeCommand Command( VmtNode entry ) => (entry.Name.ToLowerInvariant(), entry.IsBlock) switch
	{
		("dsp", false ) => new() { Kind = "dsp", Value = LambdaWarsEntityNumbers.Read( entry.Value ) },
		("dsp_volume", false ) => new() { Kind = "dsp_volume", Value = LambdaWarsEntityNumbers.Read( entry.Value ) },
		("fadetime", false ) => new() { Kind = "fadetime", Value = LambdaWarsEntityNumbers.Read( entry.Value ) },
		("playlooping", true ) => Sound( entry, true ),
		("playrandom", true ) => Sound( entry, false ),
		("playsoundscape", true ) => PlaySoundscape( entry ),
		_ => null
	};

	private static SourceSoundscapeCommand Sound( VmtNode block, bool looping )
	{
		var volume = default( SourceInterval );
		var pitch = new SourceInterval( 100, 0 );
		var soundLevel = looping ? new SourceInterval( 75, 0 ) : default;
		var time = default( SourceInterval );
		var waves = new List<string>();
		var position = -1;
		var randomPosition = false;
		Vector3? origin = null;
		var radius = 0f;

		foreach ( var key in block.Children )
		{
			if ( key.IsBlock )
			{
				if ( !looping && key.Name.Equals( "rndwave", StringComparison.OrdinalIgnoreCase ) )
					waves = key.Children.Where( wave => !wave.IsBlock ).Select( wave => Wave( wave.Value ) ).ToList();
				continue;
			}

			switch ( key.Name.ToLowerInvariant() )
			{
				case "volume": volume = SourceInterval.Read( key.Value ); break;
				case "pitch": pitch = SourceInterval.Read( key.Value ); break;
				case "time": time = SourceInterval.Read( key.Value ); break;
				case "wave" when looping: waves = [Wave( key.Value )]; break;
				case "radius": radius = LambdaWarsEntityNumbers.Read( key.Value ); break;
				case "origin": origin = ReadOrigin( key.Value ); break;
				case "attenuation": soundLevel = ReadAttenuation( key.Value ); break;
				case "soundlevel": soundLevel = ReadSoundLevel( key.Value ); break;
				case "position":
					randomPosition = key.Value.Equals( "random", StringComparison.OrdinalIgnoreCase );
					position = randomPosition ? -1 : (int)LambdaWarsEntityNumbers.Read( key.Value );
					break;
			}
		}

		return new()
		{
			Kind = looping ? "playlooping" : "playrandom", Volume = volume, Pitch = pitch, SoundLevel = soundLevel, Time = time,
			Waves = waves, Position = position, RandomPosition = randomPosition, Origin = origin, Radius = radius
		};
	}

	private static SourceSoundscapeCommand PlaySoundscape( VmtNode block )
	{
		var volume = new SourceInterval( 1, 0 );
		var position = 0;
		var positionOverride = -1;
		var ambientPositionOverride = -1;
		var name = "";

		foreach ( var key in block.Children.Where( key => !key.IsBlock ) )
		{
			switch ( key.Name.ToLowerInvariant() )
			{
				case "name": name = key.Value; break;
				case "volume": volume = SourceInterval.Read( key.Value ); break;
				case "position": position = (int)LambdaWarsEntityNumbers.Read( key.Value ); break;
				case "positionoverride": positionOverride = (int)LambdaWarsEntityNumbers.Read( key.Value ); break;
				case "ambientpositionoverride": ambientPositionOverride = (int)LambdaWarsEntityNumbers.Read( key.Value ); break;
			}
		}

		return new()
		{
			Kind = "playsoundscape", Name = name, Volume = volume, Position = position,
			PositionOverride = positionOverride, AmbientPositionOverride = ambientPositionOverride
		};
	}

	/// <summary>Lists every event a soundscape's commands can start: its positional level range, its unattenuated ambient form, and whether it loops.</summary>
	internal static IEnumerable<(string Wave, int Level, bool Loop)> Voices( SourceSoundscapeCommand command )
	{
		if ( command.Kind is not ("playlooping" or "playrandom") )
			yield break;

		var loop = command.Kind == "playlooping";
		var first = (int)Math.Min( command.SoundLevel.Start, command.SoundLevel.Start + command.SoundLevel.Range );
		var last = (int)Math.Max( command.SoundLevel.Start, command.SoundLevel.Start + command.SoundLevel.Range );
		foreach ( var wave in command.Waves )
		{
			yield return (wave, 0, loop);
			for ( var level = first; level <= last; level++ )
				yield return (wave, level, loop);
		}
	}

	private static string Wave( string wave ) => wave.Replace( '\\', '/' ).TrimStart( WavePrefixes.ToCharArray() ).Trim().ToLowerInvariant();

	private static Vector3 ReadOrigin( string text )
	{
		var parts = text.Split( ',', StringSplitOptions.RemoveEmptyEntries );
		return new( Axis( 0 ), Axis( 1 ), Axis( 2 ) );

		float Axis( int index ) => index < parts.Length ? LambdaWarsEntityNumbers.Read( parts[index] ) : 0;
	}

	private static SourceInterval ReadAttenuation( string text )
	{
		var attenuation = SourceInterval.Read( text );
		var start = AttenuationLevel( attenuation.Start );
		return new( start, AttenuationLevel( attenuation.Start + attenuation.Range ) - start );
	}

	private static int AttenuationLevel( float attenuation ) => attenuation != 0 ? (int)(50 + 20 / attenuation) : 0;

	private static SourceInterval ReadSoundLevel( string text ) => text.StartsWith( "SNDLVL_", StringComparison.OrdinalIgnoreCase )
		? new( SoundLevelName( text ), 0 ) : SourceInterval.Read( text );

	private static float SoundLevelName( string text )
	{
		const float normal = 75;
		switch ( text.ToUpperInvariant() )
		{
			case "SNDLVL_NONE": return 0;
			case "SNDLVL_IDLE" or "SNDLVL_TALKING": return 60;
			case "SNDLVL_STATIC": return 66;
			case "SNDLVL_NORM": return normal;
			case "SNDLVL_GUNFIRE": return 140;
		}

		return text.EndsWith( "dB", StringComparison.OrdinalIgnoreCase )
			&& float.TryParse( text[7..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var level ) ? level : normal;
	}
}

using System;
using System.IO;
using Sandbox.Mounting;

namespace Sandbox.Mounting.Source1;

public abstract partial class BaseMount
{
	/// <summary>Publishes the installed soundscape scripts as one document and registers a lazy sound event for every voice they can start.</summary>
	private void MountSoundscapes( MountContext context, FileIndex files, string mapId )
	{
		Dictionary<string, IReadOnlyList<SourceSoundscapeCommand>> soundscapes;
		try
		{
			soundscapes = SourceSoundscapeScripts.Read( Read, files.Files.ContainsKey, mapId, message => Log.Trace( message ) );
		}
		catch ( Exception exception ) when ( exception is IOException or InvalidDataException or NotSupportedException )
		{
			Log.Trace( $"{Title}: soundscapes were not registered: {exception.Message}" );
			return;
		}

		var voices = soundscapes.Values.SelectMany( commands => commands ).SelectMany( SourceSoundscapeScripts.Voices ).ToHashSet();
		foreach ( var (wave, level, loop) in voices.Where( voice => files.Files.ContainsKey( $"sound/{voice.Wave}" ) ) )
		{
			context.Add( ResourceType.GameResource, $"sounds/soundscapes/{wave}.{level}{(loop ? ".loop" : "")}.sound",
				new SourceSoundscapeSoundLoader( wave, level, loop ) );
		}

		context.Add( ResourceType.GameResource, "soundscapes.json", new SourceSoundscapeLibraryLoader( Json.Serialize( new { Version = 1, Soundscapes = soundscapes } ) ) );

		string Read( string path )
		{
			using var stream = files.Files[path].Open();
			using var reader = new StreamReader( stream );
			return reader.ReadToEnd();
		}
	}
}

internal sealed class SourceSoundscapeLibraryLoader( string json ) : ResourceLoader<BaseMount>
{
	protected override object Load() => json;
}

internal sealed class SourceSoundscapeSoundLoader( string wave, int level, bool loop ) : ResourceLoader<BaseMount>
{
	protected override object Load() => SourceSoundEvents.CreateSoundscape( Host, $"sound/{wave}", level, loop );
}

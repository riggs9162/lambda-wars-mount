using Sandbox.Mounting;
using System;

namespace Sandbox.Mounting.Source1;

internal sealed class WavSoundLoader( string fullPath ) : ResourceLoader<BaseMount>
{
	protected override object Load() => SoundFile.FromWav( Path, Host.GetFileBytes( fullPath ) );
}

internal sealed class Mp3SoundLoader( string fullPath ) : ResourceLoader<BaseMount>
{
	protected override object Load() => SoundFile.FromMp3( Path, Host.GetFileBytes( fullPath ) );
}

internal static class SourceSoundEvents
{
	/// <summary>Decodes Source's close/far or front/rear PCM channels as separate mono files without writing original media to disk. A mono file marked this way plays as it is, as it does in Source.</summary>
	private static SoundFile StereoChannel(BaseMount host, string wave, bool right, bool directional)
	{
		using var stream = new System.IO.MemoryStream(host.GetFileBytes(wave), false);
		using var reader = new System.IO.BinaryReader(stream);
		if (reader.ReadUInt32() != 0x46464952 || reader.ReadUInt32() > stream.Length - 8 || reader.ReadUInt32() != 0x45564157)
			throw new System.IO.InvalidDataException($"{wave}: invalid stereo-variant WAV header.");

		ushort channels = 0, bits = 0, format = 0;
		uint rate = 0;
		byte[] samples = null;
		while (stream.Position + 8 <= stream.Length)
		{
			var chunk = reader.ReadUInt32();
			var size = reader.ReadUInt32();
			var end = checked(stream.Position + size);
			if (end > stream.Length)
				throw new System.IO.InvalidDataException($"{wave}: WAV chunk exceeds its file.");
			if (chunk == 0x20746d66 && size >= 16)
			{
				format = reader.ReadUInt16();
				channels = reader.ReadUInt16();
				rate = reader.ReadUInt32();
				reader.ReadUInt32();
				reader.ReadUInt16();
				bits = reader.ReadUInt16();
			}
			else if (chunk == 0x61746164)
				samples = reader.ReadBytes(checked((int)size));
			stream.Position = end + (size & 1);
		}
		if (format == 1 && channels == 1)
			return host.LoadContentSound(wave);
		if (format != 1 || channels != 2 || bits != 16 || rate is < 8000 or > 192000 || samples is null || samples.Length % 4 != 0)
			throw new NotSupportedException($"{wave}: stereo variants require stereo 16-bit PCM WAV data.");

		var mono = new byte[samples.Length / 2];
		for (var i = 0; i < mono.Length / 2; i++)
		{
			var source = i * 4 + (right ? 2 : 0);
			mono[i * 2] = samples[source];
			mono[i * 2 + 1] = samples[source + 1];
		}
		var suffix = directional ? right ? "rear" : "front" : right ? "distant" : "close";
		return SoundFile.FromPcm($"mount://{host.Ident}/{wave}.{suffix}.vsnd", mono,
			new SoundFile.PcmOptions { Channels = 1, Rate = rate, Bits = 16 });
	}

	/// <summary>Reads a WAV's loop region in file order as Source does: "smpl" sets both ends, then "cue " moves the start. Returns null when the file has no loop.</summary>
	private static (int Start, int End)? ReadLoop( byte[] wave )
	{
		if ( wave.Length < 12 || BitConverter.ToUInt32( wave, 0 ) != 0x46464952 || BitConverter.ToUInt32( wave, 8 ) != 0x45564157 )
			return null;

		var frameSize = 0;
		long frames = 0;
		int? start = null;
		var end = 0;
		for ( long position = 12; position + 8 <= wave.Length; )
		{
			var chunk = BitConverter.ToUInt32( wave, (int)position );
			var size = BitConverter.ToUInt32( wave, (int)position + 4 );
			var body = (int)position + 8;
			if ( chunk == 0x20746d66 && size >= 16 && BitConverter.ToUInt16( wave, body ) == 1 )
				frameSize = BitConverter.ToUInt16( wave, body + 2 ) * BitConverter.ToUInt16( wave, body + 14 ) / 8;
			else if ( chunk == 0x61746164 && frameSize > 0 )
				frames = size / frameSize;
			else if ( chunk == 0x6c706d73 && size >= 60 && BitConverter.ToUInt32( wave, body + 28 ) > 0 )
			{
				start = BitConverter.ToInt32( wave, body + 44 );
				end = BitConverter.ToInt32( wave, body + 48 ) + 1;
			}
			else if ( chunk == 0x20657563 && size >= 28 && BitConverter.ToUInt32( wave, body ) > 0 )
				start = BitConverter.ToInt32( wave, body + 24 );
			position = body + (long)size + (size & 1);
		}

		if ( start is null )
			return null;
		return (start.Value, end >= frames ? 0 : end);
	}

	/// <summary>Loads a wave for looping playback, honouring the "smpl" markers most original ambient loops use and s&amp;box's WAV reader skips.</summary>
	private static SoundFile LoopingWave( BaseMount host, string wave )
	{
		var bytes = host.GetFileBytes( wave );
		return ReadLoop( bytes ) is { } loop
			? SoundFile.FromWav( $"mount://{host.Ident}/{wave}.loop.vsnd", bytes, new SoundFile.LoadOptions { LoopStart = loop.Start, LoopEnd = loop.End } )
			: host.LoadContentSound( wave );
	}

	/// <summary>Creates one soundscape voice attenuated by its script sound level. Reverb stays off because room effects come from the soundscape's own DSP.</summary>
	internal static SoundEvent CreateSoundscape( BaseMount host, string wave, int level, bool loop )
	{
		var sound = loop ? LoopingWave( host, wave ) : host.LoadContentSound( wave );
		if ( sound is null || !sound.IsValid )
			throw new InvalidOperationException( $"Soundscape wave '{wave}' could not be loaded." );
		return Create( new SourceSoundDefinition( wave, wave, [wave], (1, 1), (1, 1), level, false, true ), [sound] );
	}

	internal static SoundEvent Create( BaseMount host, SourceSoundDefinition definition, bool distant = false )
	{
		var sounds = new List<SoundFile>();
		foreach ( var wave in definition.Waves )
		{
			var sound = definition.DistanceVariant || definition.DirectionalVariant
				? StereoChannel(host, wave, distant, definition.DirectionalVariant) : host.LoadContentSound( wave );
			if ( sound is null || !sound.IsValid )
				throw new InvalidOperationException( $"{definition.SourcePath}: wave '{wave}' could not be loaded." );
			sounds.Add( sound );
		}
		return Create( definition, sounds );
	}

	private static SoundEvent Create( SourceSoundDefinition definition, List<SoundFile> sounds )
	{
		var distance = definition.AudibleDistance();
		var frames = definition.SampleFalloff( distance )
			.Select( frame => new Curve.Frame( frame.Time, frame.Value ) { Mode = Curve.HandleMode.Linear } ).ToArray();
		return new SoundEvent
		{
			Sounds = sounds,
			Volume = new RangedFloat( definition.Volume.Min, definition.Volume.Max ),
			Pitch = new RangedFloat( definition.Pitch.Min, definition.Pitch.Max ),
			SelectionMode = SoundEvent.SoundSelectionMode.Random,
			ReverbEnabled = !definition.Dry,
			AirAbsorption = false,
			DistanceAttenuation = definition.SoundLevel != 0,
			Distance = distance,
			Falloff = new Curve( frames )
		};
	}
}

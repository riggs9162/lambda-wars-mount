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
	/// <summary>Decodes Source's close-left/distant-right PCM gunshots as separate mono files without writing original media to disk.</summary>
	private static SoundFile DistanceChannel(BaseMount host, string wave, bool distant)
	{
		using var stream = new System.IO.MemoryStream(host.GetFileBytes(wave), false);
		using var reader = new System.IO.BinaryReader(stream);
		if (reader.ReadUInt32() != 0x46464952 || reader.ReadUInt32() > stream.Length - 8 || reader.ReadUInt32() != 0x45564157)
			throw new System.IO.InvalidDataException($"{wave}: invalid distance-variant WAV header.");

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
		if (format != 1 || channels != 2 || bits != 16 || rate is < 8000 or > 192000 || samples is null || samples.Length % 4 != 0)
			throw new NotSupportedException($"{wave}: distance variants require stereo 16-bit PCM WAV data.");

		var mono = new byte[samples.Length / 2];
		for (var i = 0; i < mono.Length / 2; i++)
		{
			var source = i * 4 + (distant ? 2 : 0);
			mono[i * 2] = samples[source];
			mono[i * 2 + 1] = samples[source + 1];
		}
		return SoundFile.FromPcm($"mount://{host.Ident}/{wave}.{(distant ? "distant" : "close")}.vsnd", mono,
			new SoundFile.PcmOptions { Channels = 1, Rate = rate, Bits = 16 });
	}

	internal static SoundEvent Create( BaseMount host, SourceSoundDefinition definition, bool distant = false )
	{
		var sounds = new List<SoundFile>();
		foreach ( var wave in definition.Waves )
		{
			var sound = definition.DistanceVariant ? DistanceChannel(host, wave, distant) : host.LoadContentSound( wave );
			if ( sound is null || !sound.IsValid )
				throw new InvalidOperationException( $"{definition.SourcePath}: wave '{wave}' could not be loaded." );
			sounds.Add( sound );
		}
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

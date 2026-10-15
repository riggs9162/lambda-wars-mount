using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Sandbox.Mounting.Source1;

internal sealed partial class MdlFile
{
	internal SourceSequenceEvent[] ReadSequenceEvents(Stream stream, int sequenceIndex)
	{
		var basis = checked(_header.LocalSeqIndex + (long)sequenceIndex * Marshal.SizeOf<StudioSeqDesc>());
		ValidateRange<StudioSeqDesc>(stream, basis, 1);
		stream.Position = basis;
		var sequence = LumpReader<StudioSeqDesc>.ReadSingleFromStream(stream);
		if (sequence.NumEvents is < 0 or > 4096)
			throw new InvalidDataException("Invalid original sequence event count.");

		using var data = new ReferencePoseData(stream, 0, _header.Length);
		return Enumerable.Range(0, sequence.NumEvents).Select(index =>
		{
			var offset = checked(basis + sequence.EventIndex + index * 80L);
			var cycle = data.Float(offset);
			if (cycle is < 0 or > 1)
				throw new InvalidDataException("Invalid original animation event cycle.");
			var bytes = new List<byte>();
			for (var character = 0; character < 64; character++)
			{
				var value = data.Byte(offset + 12 + character);
				if (value == 0)
					break;
				bytes.Add(value);
			}

			return new SourceSequenceEvent
			{
				Cycle = cycle,
				Id = data.Int(offset + 4),
				Name = ReadOptionalRelativeString(stream, offset, data.Int(offset + 76)),
				Options = Encoding.UTF8.GetString(bytes.ToArray())
			};
		}).ToArray();
	}
}

using System;
using System.IO;

namespace Sandbox.Mounting.Source1;

internal sealed partial class MdlFile
{
	internal sealed record SequenceAutoLayer(int SequenceIndex, int PoseParameterIndex, int Flags, float Start, float Peak, float Tail, float End);
	internal SequenceAutoLayer[] ReadSequenceAutoLayers(Stream stream, int index)
	{
		if ((uint)index >= _header.NumLocalSeq)
		{
			throw new ArgumentOutOfRangeException(nameof(index));
		}

		var offset = checked(_header.LocalSeqIndex + (long)index * System.Runtime.InteropServices.Marshal.SizeOf<StudioSeqDesc>());
		ValidateRange<StudioSeqDesc>(stream, offset, 1);
		stream.Position = offset;
		var sequence = LumpReader<StudioSeqDesc>.ReadSingleFromStream(stream);
		if (sequence.NumAutoLayers < 0 || sequence.NumAutoLayers > 256)
		{
			throw new InvalidDataException("Invalid sequence auto-layer count.");
		}

		using var data = new ReferencePoseData(stream, 0, _header.Length);
		var result = new SequenceAutoLayer[sequence.NumAutoLayers];

		for (var layer = 0; layer < result.Length; layer++)
		{
			var start = checked(offset + sequence.AutoLayerIndex + layer * 24L);
			var target = data.Short(start);
			var pose = data.Short(start + 2);
			var flags = data.Int(start + 4);
			if (target < 0 || target >= _header.NumLocalSeq)
				throw new InvalidDataException("Invalid sequence auto-layer target.");
			var begin = BitConverter.Int32BitsToSingle(data.Int(start + 8));
			var peak = BitConverter.Int32BitsToSingle(data.Int(start + 12));
			var tail = BitConverter.Int32BitsToSingle(data.Int(start + 16));
			var end = BitConverter.Int32BitsToSingle(data.Int(start + 20));
			if (float.IsNaN(begin) && float.IsNaN(peak) && float.IsNaN(tail) && float.IsNaN(end))
			{
				var targetOffset = _header.LocalSeqIndex + (long)target * System.Runtime.InteropServices.Marshal.SizeOf<StudioSeqDesc>();
				stream.Position = targetOffset;
				var targetSequence = LumpReader<StudioSeqDesc>.ReadSingleFromStream(stream);
				var staticPose = Enumerable.Range(0, targetSequence.NumBlends).All(blend =>
					GetAnimationDescriptor(stream, data.Short(targetOffset + targetSequence.AnimIndexIndex + blend * 2L)).FrameCount == 1);
				if (!staticPose && (targetSequence.Flags & 0x180) == 0)
					throw new NotSupportedException("A time-driven animation layer has a non-finite influence interval.");
				begin = peak = tail = end = 0;
			}
			if (!float.IsFinite(begin) || !float.IsFinite(peak) || !float.IsFinite(tail) || !float.IsFinite(end))
				throw new InvalidDataException("Non-finite sequence auto-layer influence interval.");
			if (target < 0 || target >= _header.NumLocalSeq || (begin != end && (begin > peak || peak > tail || tail > end)))
			{
				throw new InvalidDataException("Invalid sequence auto-layer target or influence interval.");
			}

			if ((flags & ~0x52d0) != 0)
			{
				throw new NotSupportedException($"Unsupported source auto-layer flags: {flags:x}.");
			}

			result[layer] = new(target, pose, flags, begin, peak, tail, end);
		}

		return result;
	}

	internal static (float Cycle, float Weight) SampleAutoLayerInfluence(SequenceAutoLayer layer, float cycle, float sequenceWeight, float poseValue, bool local)
	{
		if (!float.IsFinite(cycle) || !float.IsFinite(sequenceWeight) || !float.IsFinite(poseValue) || sequenceWeight < 0 || sequenceWeight > 1)
		{
			throw new ArgumentOutOfRangeException(nameof(cycle));
		}

		if (layer.Start == layer.End)
		{
			return (cycle, sequenceWeight);
		}

		var poseDriven = !local && (layer.Flags & 0x4000) != 0;
		var index = poseDriven ? poseValue : cycle;
		if (index < layer.Start || index >= layer.End)
		{
			return (cycle, 0);
		}

		var amount = 1f;
		if (index < layer.Peak && layer.Start != layer.Peak)
		{
			amount = (index - layer.Start) / (layer.Peak - layer.Start);
		}
		else if (index > layer.Tail && layer.End != layer.Tail)
		{
			amount = (layer.End - index) / (layer.End - layer.Tail);
		}

		if ((layer.Flags & 0x40) != 0)
		{
			amount = amount * amount * (3 - 2 * amount);
		}

		var weight = sequenceWeight * amount;
		if ((layer.Flags & 0x80) != 0 && index > layer.Tail)
		{
			weight = sequenceWeight == 0 ? 0 : weight / (1 - sequenceWeight + weight);
		}
		else if ((layer.Flags & 0x200) != 0)
		{
			weight = amount;
		}

		return (poseDriven ? cycle : (cycle - layer.Start) / (layer.End - layer.Start), weight);
	}
}

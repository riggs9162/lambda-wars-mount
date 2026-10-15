using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Sandbox.Mounting.Source1;

internal sealed partial class MdlFile
{
	internal sealed record SequenceDescriptor(int Index, string Name, string Activity, int Flags, int[] AnimationIndices, string UnsupportedReason)
	{
		public bool Looping => (Flags & 1) != 0;
		public int GridWidth { get; init; }
		public int GridHeight { get; init; }
		public int CyclePoseIndex { get; init; }
		public int ParameterX { get; init; }
		public int ParameterY { get; init; }
		public float[] KeysX { get; init; }
		public float[] KeysY { get; init; }
		public float[] BoneWeights { get; init; }
		public int AutoLayerCount { get; init; }
		public int IkLockCount { get; init; }
	}

	internal string[] ReadPoseParameterNames(Stream stream)
	{
		if (_header.NumLocalPoseParams is < 0 or > 256)
			throw new InvalidDataException("Invalid pose parameter count.");
		using var data = new ReferencePoseData(stream, 0, _header.Length);
		return Enumerable.Range(0, _header.NumLocalPoseParams).Select(index =>
		{
			var offset = _header.LocalPoseParamOffset + index * 20L;
			return ReadOptionalRelativeString(stream, offset, data.Int(offset));
		}).ToArray();
	}

	internal (float Minimum, float Maximum) ReadPoseParameterRange(Stream stream, int index)
	{
		if ((uint)index >= _header.NumLocalPoseParams)
			throw new InvalidDataException("Invalid cycle pose parameter index.");
		using var data = new ReferencePoseData(stream, 0, _header.Length);
		var offset = _header.LocalPoseParamOffset + index * 20L;
		return (data.Float(offset + 8), data.Float(offset + 12));
	}

	internal SequenceDescriptor[] ReadSequenceDescriptors(Stream stream)
	{
		if (_header.NumLocalSeq < 0 || _header.NumLocalSeq > 8192)
		{
			throw new InvalidDataException("Excessive local sequence count.");
		}

		ValidateRange<StudioSeqDesc>(stream, _header.LocalSeqIndex, _header.NumLocalSeq);
		var result = new SequenceDescriptor[_header.NumLocalSeq];
		using var data = new ReferencePoseData(stream, 0, _header.Length);

		for (var i = 0; i < result.Length; i++)
		{
			var offset = checked(_header.LocalSeqIndex + (long)i * Marshal.SizeOf<StudioSeqDesc>());
			stream.Position = offset;
			var sequence = LumpReader<StudioSeqDesc>.ReadSingleFromStream(stream);
			if (sequence.NumBlends == 0 && sequence.GroupSize == 0 && sequence.GroupSize2 == 0)
			{
				result[i] = new(i, ReadOptionalRelativeString(stream, offset, sequence.LabelIndex),
					ReadOptionalRelativeString(stream, offset, sequence.ActivityNameIndex), sequence.Flags, [], "Empty sequence")
				{
					KeysX = [],
					KeysY = [],
					BoneWeights = new float[BoneCount]
				};
				continue;
			}

			if (sequence.GroupSize < 1 || sequence.GroupSize2 < 1 || sequence.GroupSize > 256 || sequence.GroupSize2 > 256 || sequence.NumBlends != sequence.GroupSize * sequence.GroupSize2)
			{
				throw new InvalidDataException("Invalid sequence blend grid.");
			}

			var animations = new int[sequence.NumBlends];

			for (var blend = 0; blend < animations.Length; blend++)
			{
				animations[blend] = data.Short(checked(offset + sequence.AnimIndexIndex + blend * 2L));
				if ((uint)animations[blend] >= _header.NumLocalAnims)
				{
					throw new InvalidDataException("Sequence references an invalid local animation.");
				}
			}

			var unsupported = (sequence.Flags & 4) != 0 ? "Additive sequence" : sequence.NumAutoLayers != 0 ? "Sequence auto layers" : animations.Length != 1 ? "Pose parameter blend grid" : null;
			var weights = new float[BoneCount];

			for (var bone = 0; bone < BoneCount; bone++)
			{
				weights[bone] = data.Float(offset + sequence.WeightListIndex + bone * 4L);
				if (weights[bone] < 0 || weights[bone] > 1)
				{
					throw new InvalidDataException("Invalid sequence bone weight.");
				}
			}

			float[] Keys(int count, int axis, float minimum, float maximum)
			{
				var keys = new float[count];

				for (var key = 0; key < count; key++)
				{
					keys[key] = sequence.PoseKeyIindex != 0 ? data.Float(offset + sequence.PoseKeyIindex + (axis * sequence.GroupSize + key) * 4L) : count == 1 ? minimum : minimum + (maximum - minimum) * key / (count - 1);
					if (!float.IsFinite(keys[key]) || (key > 0 && keys[key] == keys[key - 1]))
					{
						throw new InvalidDataException($"Invalid sequence pose parameter keys: sequence {i}, axis {axis}, key {key}, value {keys[key]}, previous {(key > 0 ? keys[key - 1] : 0)}, range {minimum}..{maximum}.");
					}
				}

				return keys;
			}

			result[i] = new(i, ReadOptionalRelativeString(stream, offset, sequence.LabelIndex), ReadOptionalRelativeString(stream, offset, sequence.ActivityNameIndex), sequence.Flags, animations, unsupported)
			{
				GridWidth = sequence.GroupSize,
				GridHeight = sequence.GroupSize2,
				CyclePoseIndex = sequence.CyclePoseIndex,
				ParameterX = sequence.ParamIndex,
				ParameterY = sequence.ParamIndex2,
				KeysX = Keys(sequence.GroupSize, 0, sequence.ParamStart, sequence.ParamEnd),
				KeysY = Keys(sequence.GroupSize2, 1, sequence.ParamStart2, sequence.ParamEnd2),
				BoneWeights = weights,
				AutoLayerCount = sequence.NumAutoLayers,
				IkLockCount = sequence.NumIkLocks
			};
		}

		return result;
	}

	/// <summary>Decodes a simple original named sequence without silently discarding blend grids or auto layers.</summary>
	internal Transform[] ReadSequenceFrame(Stream stream, SequenceDescriptor sequence, int frameIndex, Func<string, Stream> openFile)
	{
		if (sequence.UnsupportedReason is not null)
		{
			throw new NotSupportedException($"Sequence '{sequence.Name}': {sequence.UnsupportedReason}.");
		}

		if (sequence.AnimationIndices.Length != 1)
		{
			throw new InvalidDataException("A simple sequence must reference exactly one animation.");
		}

		return ReadAnimationFrame(stream, sequence.AnimationIndices[0], frameIndex, openFile);
	}

	/// <summary>Returns original include model paths; callers must bound recursion and remap bones by their original names.</summary>
	internal string[] ReadAnimationIncludeModels(Stream stream)
	{
		if (_header.NumIncludeModels < 0 || _header.NumIncludeModels > 256)
		{
			throw new InvalidDataException("Excessive include model count.");
		}

		using var data = new ReferencePoseData(stream, 0, _header.Length);
		var result = new string[_header.NumIncludeModels];

		for (var i = 0; i < result.Length; i++)
		{
			var offset = checked(_header.IncludeModelOffset + i * 8L);
			var path = ReadOptionalRelativeString(stream, offset, data.Int(offset + 4)).Replace('\\', '/');
			if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains(':') || path.Split('/').Any(part => part == ".."))
			{
				throw new InvalidDataException("Invalid animation include model path.");
			}

			result[i] = path;
		}

		return result;
	}
}

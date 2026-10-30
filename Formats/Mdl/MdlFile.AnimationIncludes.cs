using System;
using System.IO;

namespace Sandbox.Mounting.Source1;

internal sealed partial class MdlFile
{
	internal sealed record IncludedSequence(string ModelPath, SequenceDescriptor Sequence, MdlFile Model, int[] RootBoneIndices);

	/// <summary>Finds original local and recursively included sequences with bounded traversal and original bone-name mapping.</summary>
	internal IncludedSequence[] ReadIncludedSequences(string modelPath, Func<string, Stream> openFile)
	{
		var result = new List<IncludedSequence>();
		var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var rootNames = Enumerable.Range(0, BoneCount).ToDictionary(GetBoneName, value => value, StringComparer.Ordinal);
		void Visit(MdlFile model, string path, int depth)
		{
			if (depth > 16 || visited.Count >= 64)
			{
				throw new InvalidDataException("Animation include graph exceeds its traversal budget.");
			}

			if (!visited.Add(path))
			{
				return;
			}

			using var stream = openFile(path);
			var mapping = Enumerable.Range(0, model.BoneCount).Select(index => rootNames.TryGetValue(model.GetBoneName(index), out var rootIndex) ? rootIndex : -1).ToArray();

			foreach (var sequence in model.ReadSequenceDescriptors(stream))
			{
				result.Add(new(path, sequence, model, mapping));
			}

			foreach (var include in model.ReadAnimationIncludeModels(stream))
			{
				if (visited.Contains(include))
				{
					continue;
				}

				using var includedStream = openFile(include);
				Visit(new MdlFile(includedStream), include, depth + 1);
			}
		}

		Visit(this, modelPath, 0);

		return result.ToArray();
	}

	/// <summary>Returns raw source bone channels in the root model's bone order; postprocessing requirements remain explicit.</summary>
	internal RawAnimationFrame ReadIncludedRawFrame(IncludedSequence sequence, int blendIndex, int frameIndex, Func<string, Stream> openFile)
	{
		if ((uint)blendIndex >= sequence.Sequence.AnimationIndices.Length)
		{
			throw new ArgumentOutOfRangeException(nameof(blendIndex));
		}

		return ReadIncludedAnimationFrame(sequence, sequence.Sequence.AnimationIndices[blendIndex], frameIndex, openFile);
	}

	/// <summary>Reads one frame of an animation in an included model, without needing the sequence that referenced it.</summary>
	internal RawAnimationFrame ReadIncludedAnimationFrame(IncludedSequence source, int animationIndex, int frameIndex, Func<string, Stream> openFile)
	{
		using var stream = openFile(source.ModelPath);
		var raw = source.Model.ReadRawAnimationFrame(stream, animationIndex, frameIndex, openFile);
		var result = Bones.Select(bone => raw.Additive ? new Transform(Vector3.Zero, Rotation.Identity) : new Transform(bone.Position, bone.Quaternion)).ToArray();

		for (var bone = 0; bone < raw.Pose.Length; bone++)
		{
			var target = source.RootBoneIndices[bone];
			if (target >= 0)
			{
				result[target] = raw.Pose[bone];
			}
		}

		return raw with
		{
			Pose = result
		};
	}
}

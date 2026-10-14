using System;
using System.IO;

namespace Sandbox.Mounting.Source1;

internal sealed partial class MdlFile
{
	internal sealed record SequenceBoneSample(Transform[] Pose, float Fps, bool Looping, bool Additive, string[] PendingOperations);

	/// <summary>Samples original bone channels and Source triangular blend weights; returned requirements identify unevaluated sequence operations.</summary>
	internal SequenceBoneSample SampleIncludedSequenceBones(IncludedSequence included, float cycle, float parameterX, float parameterY, Func<string, Stream> openFile)
	{
		return SampleIncludedSequenceBonesCore(included, cycle, parameterX, parameterY, openFile, 0);
	}

	private SequenceBoneSample SampleIncludedSequenceBonesCore(IncludedSequence included, float cycle, float parameterX, float parameterY, Func<string, Stream> openFile, int depth)
	{
		if (depth > 16)
		{
			throw new InvalidDataException("Sequence auto-layer recursion exceeds its budget.");
		}

		if (!float.IsFinite(cycle) || !float.IsFinite(parameterX) || !float.IsFinite(parameterY))
		{
			throw new ArgumentOutOfRangeException(nameof(cycle));
		}

		var sequence = included.Sequence;
		cycle = sequence.Looping ? cycle - MathF.Floor(cycle) : Math.Clamp(cycle, 0, 1);
		var(x, sx) = AnimationGridCoordinate(sequence.KeysX, parameterX);
		var(y, sy) = AnimationGridCoordinate(sequence.KeysY, parameterY);
		var blends = SequenceBlendWeights(x, y, sx, sy, sequence.GridWidth, sequence.GridHeight);
		var operations = new HashSet<string>();
		Transform[] pose = null;
		var accumulated = 0f;
		var fps = 0f;
		var additive = (sequence.Flags & 4) != 0;

		foreach (var blend in blends)
		{
			if (blend.Weight <= 0)
			{
				continue;
			}

			using var stream = openFile(included.ModelPath);
			var descriptor = included.Model.GetAnimationDescriptor(stream, sequence.AnimationIndices[blend.Index]);
			var frame = cycle * (descriptor.FrameCount - 1);
			var first = ReadIncludedRawFrame(included, blend.Index, (int)frame, openFile);
			var second = ReadIncludedRawFrame(included, blend.Index, Math.Min((int)frame + 1, descriptor.FrameCount - 1), openFile);
			if (first.IkRuleCount != 0)
			{
				var rules = included.Model.ReadAnimationIkRuleMetadata(stream, sequence.AnimationIndices[blend.Index], cycle, openFile);

				foreach (var rule in rules)
				{
					if (rule.Type != 4 && rule.Weight > 0)
					{
						operations.Add(rule.Type switch
						{
							1 => "IK self targets",
							3 => "Ground IK targets and latch state",
							5 => "Attachment IK targets",
							6 => "IK unlatch state",
							_ => "Unsupported source IK type"
						});
					}
				}
			}

			if (first.Movements.Length != 0)
			{
				operations.Add("Root motion available separately");
			}

			if (first.Additive != additive)
			{
				operations.Add("Mixed additive animation tracks");
			}

			var sample = InterpolateAnimationPose(first.Pose, second.Pose, frame - (int)frame);
			if (first.HierarchyCount != 0)
			{
				sample = EvaluateIncludedHierarchy(included, sequence.AnimationIndices[blend.Index], frame, cycle, sample, openFile);
			}

			pose = pose is null ? sample : InterpolateAnimationPose(pose, sample, blend.Weight / (accumulated + blend.Weight));
			accumulated += blend.Weight;
			fps += descriptor.Fps * blend.Weight;
		}

		if (sequence.AutoLayerCount != 0)
		{
			using var stream = openFile(included.ModelPath);
			var layers = included.Model.ReadSequenceAutoLayers(stream, sequence.Index);
			var descriptors = included.Model.ReadSequenceDescriptors(stream);

			foreach (var local in new[]
			{
				true,
				false
			}

			)
			{
				if (local && (sequence.Flags & 0x200) == 0)
				{
					continue;
				}

				foreach (var layer in layers)
				{
					if (((layer.Flags & 0x1000) != 0) != local)
					{
						continue;
					}

					var poseValue = layer.PoseParameterIndex == sequence.ParameterX ? parameterX : layer.PoseParameterIndex == sequence.ParameterY ? parameterY : 0;
					if (!local && (layer.Flags & 0x4000) != 0 && layer.PoseParameterIndex != sequence.ParameterX && layer.PoseParameterIndex != sequence.ParameterY)
					{
						operations.Add($"Auto-layer pose parameter {layer.PoseParameterIndex} requires a value");
						continue;
					}

					var influence = SampleAutoLayerInfluence(layer, cycle, 1, poseValue, local);
					if (influence.Weight == 0)
					{
						continue;
					}

					var descriptor = descriptors[layer.SequenceIndex];
					var target = included with
					{
						Sequence = descriptor
					};
					var targetX = descriptor.ParameterX == sequence.ParameterX ? parameterX : descriptor.ParameterX == sequence.ParameterY ? parameterY : 0;
					var targetY = descriptor.ParameterY == sequence.ParameterX ? parameterX : descriptor.ParameterY == sequence.ParameterY ? parameterY : 0;
					var sampled = SampleIncludedSequenceBonesCore(target, influence.Cycle, targetX, targetY, openFile, depth + 1);
					pose = ApplySequenceBoneSample(target, sampled, pose, influence.Weight);

					foreach (var pending in sampled.PendingOperations)
					{
						if (pending != "Additive sequence requires a base pose")
						{
							operations.Add(pending);
						}
					}
				}
			}
		}

		if (sequence.IkLockCount != 0)
		{
			operations.Add("Sequence IK locks");
		}

		if (additive)
		{
			operations.Add("Additive sequence requires a base pose");
		}

		return new(pose ?? throw new InvalidDataException("No sequence blend contributed."), fps, sequence.Looping, additive, operations.ToArray());
	}

	internal Transform[] ApplySequenceBoneSample(IncludedSequence included, SequenceBoneSample sample, Transform[] basePose, float weight)
	{
		if (basePose.Length != BoneCount || sample.Pose.Length != BoneCount || !float.IsFinite(weight) || weight < 0 || weight > 1)
		{
			throw new ArgumentException("Invalid sequence blend input.");
		}

		var result = basePose.ToArray();

		for (var sourceBone = 0; sourceBone < included.RootBoneIndices.Length; sourceBone++)
		{
			var bone = included.RootBoneIndices[sourceBone];
			if (bone < 0)
			{
				continue;
			}

			var amount = included.Sequence.BoneWeights[sourceBone] * weight;
			var source = sample.Pose[bone];
			if (sample.Additive)
			{
				var rotation = Rotation.Slerp(Rotation.Identity, source.Rotation, amount);
				result[bone] = new Transform(basePose[bone].Position + source.Position * amount, (included.Sequence.Flags & 0x10) != 0 ? basePose[bone].Rotation * rotation : rotation * basePose[bone].Rotation);
			}
			else
			{
				result[bone] = new Transform(basePose[bone].Position + (source.Position - basePose[bone].Position) * amount, Rotation.Slerp(basePose[bone].Rotation, source.Rotation, amount));
			}
		}

		return result;
	}

	private static Transform[] InterpolateAnimationPose(Transform[] first, Transform[] second, float amount)
	{
		if (first.Length != second.Length)
		{
			throw new InvalidDataException("Animation skeleton size changed.");
		}

		var result = new Transform[first.Length];

		for (var bone = 0; bone < result.Length; bone++)
		{
			result[bone] = new Transform(first[bone].Position + (second[bone].Position - first[bone].Position) * amount, Rotation.Slerp(first[bone].Rotation, second[bone].Rotation, amount));
		}

		return result;
	}

	private static (int Index, float Fraction) AnimationGridCoordinate(float[] keys, float value)
	{
		if (keys.Length == 1)
		{
			return (0, 0);
		}

		var increasing = keys[^1] > keys[0];

		for (var key = 0; key < keys.Length - 1; key++)
		{
			if ((increasing && value <= keys[key + 1]) || (!increasing && value >= keys[key + 1]))
			{
				return (key, Math.Clamp((value - keys[key]) / (keys[key + 1] - keys[key]), 0, 1));
			}
		}

		return (keys.Length - 2, 1);
	}

	private static (int Index, float Weight)[] SequenceBlendWeights(int x, int y, float sx, float sy, int width, int height)
	{
		int Index(int dx, int dy) => (y + dy) * width + x + dx;
		if (width == 1 && height == 1)
		{
			return[(0, 1)];
		}

		if (height == 1)
		{
			return[(Index(0, 0), 1 - sx), (Index(1, 0), sx)];
		}

		if (width == 1)
		{
			return[(Index(0, 0), 1 - sy), (Index(0, 1), sy)];
		}

		if (((x + y) & 1) == 0)
		{
			return sx > sy ? [(Index(0, 0), 1 - sx), (Index(1, 0), sx - sy), (Index(1, 1), sy)] : [(Index(1, 1), sx), (Index(0, 1), sy - sx), (Index(0, 0), 1 - sy)];
		}

		return sx + sy > 1 ? [(Index(1, 0), 1 - sy), (Index(1, 1), sx + sy - 1), (Index(0, 1), 1 - sx)] : [(Index(0, 1), sy), (Index(0, 0), 1 - sx - sy), (Index(1, 0), sx)];
	}
}

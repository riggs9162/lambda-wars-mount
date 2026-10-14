using System;
using System.IO;

namespace Sandbox.Mounting.Source1;

internal sealed partial class MdlFile
{
	internal Transform[] EvaluateIncludedHierarchy(IncludedSequence included, int animationIndex, float frame, float cycle, Transform[] pose, Func<string, Stream> openFile)
	{
		using var stream = openFile(included.ModelPath);
		var model = included.Model;
		var offset = model.AnimationDescriptorOffset(animationIndex);
		stream.Position = offset;
		var animation = LumpReader<StudioAnimDesc>.ReadSingleFromStream(stream);
		if (animation.NumLocalHierarchy == 0)
		{
			return pose;
		}

		if (animation.NumLocalHierarchy < 0 || animation.NumLocalHierarchy > model.BoneCount || animation.LocalHierarchyIndex <= 0)
		{
			throw new InvalidDataException("Invalid local hierarchy records.");
		}

		var result = pose.ToArray();
		model.WithAnimationAuxiliaryData(stream, animation, offset, openFile, (data, baseOffset) =>
		{
			for (var entry = 0; entry < animation.NumLocalHierarchy; entry++)
			{
				var record = checked(baseOffset + animation.LocalHierarchyIndex + entry * 48L);
				var sourceBone = data.Int(record);
				var sourceParent = data.Int(record + 4);
				var start = data.Float(record + 8);
				var peak = data.Float(record + 12);
				var tail = data.Float(record + 16);
				var end = data.Float(record + 20);
				var firstFrame = data.Int(record + 24);
				var compressed = data.Int(record + 28);
				if ((uint)sourceBone >= model.BoneCount || sourceParent < -1 || sourceParent >= model.BoneCount || compressed <= 0 || start > peak || peak > tail || tail > end)
				{
					throw new InvalidDataException("Invalid local hierarchy bone or influence interval.");
				}

				var bone = included.RootBoneIndices[sourceBone];
				var parent = sourceParent < 0 ? -1 : included.RootBoneIndices[sourceParent];
				if (bone < 0 || (sourceParent >= 0 && parent < 0))
				{
					continue;
				}

				var weight = 1f;
				if (tail - peak < 1)
				{
					var index = end > 1 && cycle < start ? cycle + 1 : cycle;
					if (index < start || index >= end)
					{
						continue;
					}

					if (index < peak && start != peak)
					{
						weight = (index - start) / (peak - start);
					}
					else if (index > tail && end != tail)
					{
						weight = (end - index) / (end - tail);
					}

					weight = weight * weight * (3 - 2 * weight);
				}

				var local = SampleCompressedAnimationTransform(data, record + compressed, frame - firstFrame);
				var world = AnimationWorldPose(result);
				var replacementWorld = parent < 0 ? local : world[parent].ToWorld(local);
				var originalParent = Bones[bone].Parent;
				var replacement = originalParent < 0 ? replacementWorld : world[originalParent].ToLocal(replacementWorld);
				result[bone] = new Transform(result[bone].Position + (replacement.Position - result[bone].Position) * weight, Rotation.Slerp(result[bone].Rotation, replacement.Rotation, weight));
			}
		});

		return result;
	}

	private Transform[] AnimationWorldPose(Transform[] pose)
	{
		var result = new Transform[BoneCount];
		var state = new byte[BoneCount];
		Transform Resolve(int bone)
		{
			if (state[bone] == 2)
			{
				return result[bone];
			}

			if (state[bone] == 1)
			{
				throw new InvalidDataException("Cyclic animation bone hierarchy.");
			}

			state[bone] = 1;
			var parent = Bones[bone].Parent;
			result[bone] = parent < 0 ? pose[bone] : Resolve(parent).ToWorld(pose[bone]);
			state[bone] = 2;

			return result[bone];
		}

		for (var bone = 0; bone < BoneCount; bone++)
		{
			Resolve(bone);
		}

		return result;
	}

	private void WithAnimationAuxiliaryData(Stream stream, StudioAnimDesc animation, long descriptorOffset, Func<string, Stream> openFile, Action<ReferencePoseData, long> action)
	{
		using var data = new ReferencePoseData(stream, 0, _header.Length);
		if (animation.AnimBlock == 0)
		{
			action(data, descriptorOffset);
			return;
		}

		if (animation.AnimBlock < 1 || animation.AnimBlock >= _header.NumAnimBlocks)
		{
			throw new InvalidDataException("Invalid auxiliary animation block.");
		}

		var start = data.Int(_header.AnimBlockOffset + animation.AnimBlock * 8L);
		var end = data.Int(_header.AnimBlockOffset + animation.AnimBlock * 8L + 4);
		var path = ReadOptionalRelativeString(stream, 0, _header.AnimBlockNameOffset).Replace('\\', '/');
		if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains(':') || path.Split('/').Any(part => part == ".."))
		{
			throw new InvalidDataException("Invalid auxiliary ANI path.");
		}

		using var external = openFile(path);
		using var externalData = new ReferencePoseData(external, start, end);
		action(externalData, 0);
	}

	private static Transform SampleCompressedAnimationTransform(ReferencePoseData data, long offset, float frame)
	{
		if (!float.IsFinite(frame) || frame < 0 || frame > 65536)
		{
			throw new InvalidDataException("Invalid compressed animation frame.");
		}

		var integerFrame = (int)frame;
		var fraction = frame - integerFrame;
		var first = new float[6];
		var second = new float[6];

		for (var axis = 0; axis < 6; axis++)
		{
			var scale = data.Float(offset + axis * 4L);
			var relative = data.Short(offset + 24 + axis * 2L);
			if (relative == 0)
			{
				continue;
			}

			if (relative < 36)
			{
				throw new InvalidDataException("Invalid compressed animation channel pointer.");
			}

			first[axis] = ReadCompressedAnimationValue(data, offset + relative, integerFrame) * scale;
			second[axis] = fraction > 0.0001f ? ReadCompressedAnimationValue(data, offset + relative, integerFrame + 1) * scale : first[axis];
		}

		var firstPosition = new Vector3(first[0], first[1], first[2]);
		var secondPosition = new Vector3(second[0], second[1], second[2]);
		var firstRotation = Rotation.From(new Angles(first[4].RadianToDegree(), first[5].RadianToDegree(), first[3].RadianToDegree()));
		var secondRotation = Rotation.From(new Angles(second[4].RadianToDegree(), second[5].RadianToDegree(), second[3].RadianToDegree()));

		return new Transform(firstPosition + (secondPosition - firstPosition) * fraction, Rotation.Lerp(firstRotation, secondRotation, fraction));
	}

	private static float ReadCompressedAnimationValue(ReferencePoseData data, long run, int frame)
	{
		while (true)
		{
			var valid = data.Byte(run);
			var total = data.Byte(run + 1);
			if (valid == 0 || total == 0 || valid > total)
			{
				throw new InvalidDataException("Invalid compressed animation RLE run.");
			}

			if (frame < total)
			{
				return data.Short(run + 2L * (Math.Min(frame, valid - 1) + 1));
			}

			frame -= total;
			run = checked(run + 2L * (valid + 1));
		}
	}
}

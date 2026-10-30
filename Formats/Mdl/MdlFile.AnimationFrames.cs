using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Sandbox.Mounting.Source1;

internal sealed partial class MdlFile
{
	internal sealed record AnimationDescriptor(int Index, string Name, float Fps, int FrameCount, int Flags)
	{
		public bool Looping => (Flags & 1) != 0;
	}

	/// <summary>The animation details a sequence catalog needs, read without decoding any frame.</summary>
	internal readonly record struct AnimationSummary(AnimationDescriptor Descriptor, int IkRuleCount, bool Additive, AnimationMovement[] Movements);

	internal int AnimationCount => _header.NumLocalAnims;

	internal AnimationSummary ReadAnimationSummary(Stream stream, int index)
	{
		var descriptor = GetAnimationDescriptor(stream, index);
		stream.Position = AnimationDescriptorOffset(index);
		var animation = LumpReader<StudioAnimDesc>.ReadSingleFromStream(stream);
		return new(descriptor, animation.NumIkRules, (animation.Flags & 4) != 0, ReadAnimationMovements(stream, index));
	}

	internal AnimationDescriptor GetAnimationDescriptor(Stream stream, int index)
	{
		var offset = AnimationDescriptorOffset(index);
		ValidateRange<StudioAnimDesc>(stream, offset, 1);
		stream.Position = offset;
		var animation = LumpReader<StudioAnimDesc>.ReadSingleFromStream(stream);
		if (animation.NumFrames < 1 || animation.NumFrames > 65536 || !float.IsFinite(animation.Fps) || animation.Fps <= 0 || animation.Fps > 1000)
		{
			throw new InvalidDataException("Invalid animation frame count or FPS.");
		}

		return new(index, ReadOptionalRelativeString(stream, offset, animation.NameIndex), animation.Fps, animation.NumFrames, animation.Flags);
	}

	private long AnimationDescriptorOffset(int index)
	{
		if ((uint)index >= _header.NumLocalAnims)
		{
			throw new ArgumentOutOfRangeException(nameof(index));
		}

		return checked(_header.LocalAnimIndex + (long)Marshal.SizeOf<StudioAnimDesc>() * index);
	}

	/// <summary>Decodes one bounded local bone frame. Additive tracks, IK, hierarchy changes and root motion are rejected rather than approximated.</summary>
	internal Transform[] ReadAnimationFrame(Stream stream, int animationIndex, int frameIndex, Func<string, Stream> openFile)
	{
		var raw = ReadRawAnimationFrame(stream, animationIndex, frameIndex, openFile);
		if (raw.RequiresPostprocessing)
		{
			throw new NotSupportedException($"Animation '{raw.Descriptor.Name}' requires additive tracks, IK, hierarchy changes or root motion.");
		}

		return raw.Pose;
	}

	internal RawAnimationFrame ReadRawAnimationFrame(Stream stream, int animationIndex, int frameIndex, Func<string, Stream> openFile)
	{
		var descriptor = GetAnimationDescriptor(stream, animationIndex);
		if ((uint)frameIndex >= descriptor.FrameCount)
		{
			throw new ArgumentOutOfRangeException(nameof(frameIndex));
		}

		var offset = AnimationDescriptorOffset(animationIndex);
		stream.Position = offset;
		var animation = LumpReader<StudioAnimDesc>.ReadSingleFromStream(stream);
		var additive = (animation.Flags & 4) != 0;
		var pose = Bones.Select(bone => additive ? new Transform(Vector3.Zero, Rotation.Identity) : new Transform(bone.Position, bone.Quaternion)).ToArray();
		using var data = new ReferencePoseData(stream, 0, _header.Length);
		if ((animation.Flags & 0x20) == 0)
		{
			var block = animation.AnimBlock;
			var index = animation.AnimIndex;
			if (animation.SectionFrames < 0)
			{
				throw new InvalidDataException("Negative animation section size.");
			}

			if (animation.SectionFrames > 0)
			{
				if (animation.SectionIndex <= 0)
				{
					throw new InvalidDataException("Missing animation sections.");
				}

				int section;
				if (animation.NumFrames > animation.SectionFrames && frameIndex == animation.NumFrames - 1)
				{
					section = animation.NumFrames / animation.SectionFrames + 1;
					frameIndex = 0;
				}
				else
				{
					section = frameIndex / animation.SectionFrames;
					frameIndex %= animation.SectionFrames;
				}

				var sectionOffset = checked(offset + animation.SectionIndex + section * 8L);
				block = data.Int(sectionOffset);
				index = data.Int(sectionOffset + 4);
			}

			if (block < 0 || index < 0 || (block == 0 && index == 0))
			{
				throw new NotSupportedException("Animation has no complete frame data.");
			}

			if (block == 0)
			{
				ReadAnimationBones(data, checked(offset + index), animation.Flags, frameIndex, pose);
			}
			else
			{
				if (block >= _header.NumAnimBlocks)
				{
					throw new InvalidDataException("Invalid animation block number.");
				}

				var start = data.Int(_header.AnimBlockOffset + block * 8L);
				var end = data.Int(_header.AnimBlockOffset + block * 8L + 4);
				var path = ReadOptionalRelativeString(stream, 0, _header.AnimBlockNameOffset).Replace('\\', '/');
				if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains(':') || path.Split('/').Any(part => part == ".."))
				{
					throw new InvalidDataException("Invalid ANI path.");
				}

				using var external = openFile(path);
				using var externalData = new ReferencePoseData(external, start, end);
				ReadAnimationBones(externalData, index, animation.Flags, frameIndex, pose);
			}
		}

		for (var bone = 0; bone < pose.Length; bone++)
		{
			var rotation = pose[bone].Rotation;
			var length = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
			if (!IsFinite(pose[bone].Position) || !float.IsFinite(length) || length < 1e-12f)
			{
				throw new InvalidDataException("Invalid animation transform.");
			}

			pose[bone].Rotation = rotation.Normal;
		}

		return new(descriptor, pose, animation.NumIkRules, animation.NumLocalHierarchy, ReadAnimationMovements(stream, animationIndex), additive);
	}

	private static Vector3 AnimationChannels(ReferencePoseData data, long offset, int frame) => new(AnimationChannel(data, offset, 0, frame), AnimationChannel(data, offset, 1, frame), AnimationChannel(data, offset, 2, frame));
	private static float AnimationChannel(ReferencePoseData data, long offset, int axis, int frame)
	{
		var relative = data.Short(offset + axis * 2);
		if (relative == 0)
		{
			return 0;
		}

		if (relative < 6)
		{
			throw new InvalidDataException("Invalid animation channel pointer.");
		}

		var run = checked(offset + relative);

		while (true)
		{
			var valid = data.Byte(run);
			var total = data.Byte(run + 1);
			if (valid == 0 || total == 0 || valid > total)
			{
				throw new InvalidDataException("Invalid animation RLE run.");
			}

			if (frame < total)
			{
				return data.Short(run + 2L * (Math.Min(frame, valid - 1) + 1));
			}

			frame -= total;
			run = checked(run + 2L * (valid + 1));
		}
	}

	private void ReadAnimationBones(ReferencePoseData data, long offset, int animationFlags, int frameIndex, Transform[] pose)
	{
		if ((animationFlags & 0x0040) != 0)
		{
			ReadAnimationPackedFrame(data, offset, frameIndex, pose);
			return;
		}

		var previous = -1;

		while (true)
		{
			var index = data.Byte(offset);
			var flags = data.Byte(offset + 1);
			var next = data.Short(offset + 2);
			if (index == 255 && flags == 0 && next == 0)
			{
				break;
			}

			if (index >= BoneCount || index <= previous || next < 0 || (next != 0 && next < 4))
			{
				throw new InvalidDataException("Invalid reference animation bone record.");
			}

			if ((flags & ~0x3f) != 0 || (flags & 5) == 5 || ((flags & 2) != 0 && (flags & 0x28) != 0) || (flags & 0x28) == 0x28)
			{
				throw new InvalidDataException("Invalid reference animation channel flags.");
			}

			var bone = Bones[index];
			var delta = (flags & 0x10) != 0;
			var position = delta ? Vector3.Zero : bone.Position;
			var rotation = delta ? Rotation.Identity : bone.Quaternion;
			var payload = offset + 4;
			if ((flags & 2) != 0)
			{
				rotation = data.Quaternion48(payload);
			}

			if ((flags & 0x20) != 0)
			{
				rotation = data.Quaternion64(payload);
			}

			if ((flags & 1) != 0)
			{
				position = data.Vector48(payload + ((flags & 2) != 0 ? 6 : (flags & 0x20) != 0 ? 8 : 0));
			}

			if ((flags & 4) != 0)
			{
				position += AnimationChannels(data, payload + ((flags & 8) != 0 ? 6 : 0), frameIndex) * bone.PositionScale;
			}

			if ((flags & 8) != 0)
			{
				var angles = (delta ? Vector3.Zero : bone.Rotation) + AnimationChannels(data, payload, frameIndex) * bone.RotationScale;
				rotation = Rotation.From(new Angles(angles.y.RadianToDegree(), angles.z.RadianToDegree(), angles.x.RadianToDegree()));
			}

			pose[index] = new Transform(position, rotation);
			if (next == 0)
			{
				break;
			}

			previous = index;
			offset += next;
		}
	}

	private void ReadAnimationPackedFrame(ReferencePoseData data, long offset, int frameIndex, Transform[] pose)
	{
		var constants = data.Int(offset);
		var frames = data.Int(offset + 4);
		var stride = data.Int(offset + 8);
		var constantSize = 0;
		var frameSize = 0;

		for (var i = 0; i < BoneCount; i++)
		{
			var flags = data.Byte(offset + 24 + i);
			var positionFlags = flags & 0x35;
			var rotationFlags = flags & 0xca;
			if ((positionFlags & (positionFlags - 1)) != 0 || (rotationFlags & (rotationFlags - 1)) != 0)
			{
				throw new InvalidDataException("Conflicting reference frame channels.");
			}

			if ((flags & 0x88) != 0)
			{
				frameSize += 6;
			}

			if ((flags & 4) != 0)
			{
				frameSize += 6;
			}

			if ((flags & 0x10) != 0)
			{
				frameSize += 12;
			}

			if ((flags & 1) != 0)
			{
				constantSize += 6;
			}

			if ((flags & 0x42) != 0)
			{
				constantSize += 6;
			}

			if ((flags & 0x20) != 0)
			{
				constantSize += 12;
			}
		}

		if (stride < frameSize || constants < 0 || frames < 0 || (constantSize > 0 && constants < 24 + BoneCount) || (frameSize > 0 && frames < 24 + BoneCount))
		{
			throw new InvalidDataException("Invalid reference frame offsets or stride.");
		}

		var constant = offset + constants;
		var frame = checked(offset + frames + (long)stride * frameIndex);

		for (var i = 0; i < BoneCount; i++)
		{
			var flags = data.Byte(offset + 24 + i);
			var position = pose[i].Position;
			var rotation = pose[i].Rotation;
			if ((flags & 0x40) != 0)
			{
				rotation = data.Quaternion48S(constant);
				constant += 6;
			}

			if ((flags & 0x20) != 0)
			{
				position = data.Vector96(constant);
				constant += 12;
			}

			if ((flags & 2) != 0)
			{
				rotation = data.Quaternion48(constant);
				constant += 6;
			}

			if ((flags & 1) != 0)
			{
				position = data.Vector48(constant);
				constant += 6;
			}

			if ((flags & 0x88) != 0)
			{
				rotation = (flags & 0x80) != 0 ? data.Quaternion48S(frame) : data.Quaternion48(frame);
				frame += 6;
			}

			if ((flags & 0x14) != 0)
			{
				position = (flags & 0x10) != 0 ? data.Vector96(frame) : data.Vector48(frame);
				frame += (flags & 0x10) != 0 ? 12 : 6;
			}

			pose[i] = new Transform(position, rotation);
		}
	}
}
